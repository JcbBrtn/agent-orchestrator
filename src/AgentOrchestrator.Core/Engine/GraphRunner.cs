using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Providers;
using AgentOrchestrator.Core.Tools;
using Microsoft.Extensions.AI;

namespace AgentOrchestrator.Core.Engine;

public class GraphRunnerOptions
{
    public required IChatClientFactory ChatClientFactory { get; init; }
    public required Func<string, ProviderConfig?> ProviderLookup { get; init; }
    public Func<string, SkillDefinition?> SkillLookup { get; init; } = _ => null;
    public IApprovalService Approval { get; init; } = new AutoApproveService();
    public IUserInputService UserInput { get; init; } = new NoUserInputService();
    public RunLogger? Logger { get; init; }
    public HttpClient? HttpClient { get; init; }
}

/// <summary>
/// Executes a graph: activations are scheduled from a queue (in parallel when enabled), outgoing arrows are evaluated
/// after each agent completes, and loops are bounded by per-arrow MaxTraversals and the graph's MaxTotalSteps.
/// </summary>
public class GraphRunner(GraphRunnerOptions options)
{
    private sealed record Activation(string Id, AgentDefinition Agent, List<AgentInput> Inputs);

    public async Task<RunResult> RunAsync(
        GraphDefinition graph,
        RunRequest request,
        Action<RunEvent>? onEvent = null,
        PauseTokenSource? pause = null,
        CancellationToken cancellationToken = default)
    {
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        pause ??= new PauseTokenSource();
        var logDir = options.Logger?.BeginRun(runId, graph, request);

        void Emit(RunEvent e)
        {
            options.Logger?.Write(runId, e);
            onEvent?.Invoke(e);
        }

        void PauseChanged(object? _, bool paused) =>
            Emit(new RunEvent { Kind = paused ? RunEventKind.Paused : RunEventKind.Resumed, RunId = runId });
        pause.PausedChanged += PauseChanged;

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = runCts.Token;

        var context = new RunContext
        {
            RunId = runId,
            Graph = graph,
            Request = request,
            Toolbox = new AgentToolbox(request.WorkingDirectory, options.HttpClient),
            Emit = Emit,
            Pause = pause,
            Approval = options.Approval,
            UserInput = options.UserInput,
            SkillLookup = options.SkillLookup,
        };
        var executor = new AgentExecutor(context);

        var queue = new Queue<Activation>();
        var running = new Dictionary<Task<AgentResult>, Activation>();
        var joinBuffers = new Dictionary<string, List<AgentInput>>();
        var traversals = new Dictionary<string, int>();
        var finalOutputs = new List<RunOutput>();
        var steps = 0;
        var activationCounter = 0;
        string? error = null;
        var status = RunStatus.Completed;

        Emit(new RunEvent { Kind = RunEventKind.RunStarted, RunId = runId, Text = request.Task });

        try
        {
            var entry = graph.FindAgent(request.EntryAgentId)
                ?? throw new InvalidOperationException("Entry agent not found in graph.");
            queue.Enqueue(new Activation(NextId(), entry, [new AgentInput(null, "User", request.Task, null)]));

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                while (queue.Count > 0 && (graph.RunBranchesInParallel || running.Count == 0))
                {
                    if (steps >= graph.MaxTotalSteps)
                    {
                        Emit(new RunEvent { Kind = RunEventKind.Warning, RunId = runId, Text = $"Max total steps ({graph.MaxTotalSteps}) reached; {queue.Count} pending activation(s) dropped." });
                        queue.Clear();
                        joinBuffers.Clear();
                        break;
                    }
                    var activation = queue.Dequeue();
                    steps++;
                    running[StartActivation(activation)] = activation;
                }

                if (running.Count == 0)
                {
                    if (FlushJoins()) continue;
                    break;
                }

                var done = await Task.WhenAny(running.Keys);
                var completed = running[done];
                running.Remove(done);

                AgentResult result;
                try
                {
                    result = await done;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var message = ex.GetBaseException().Message;
                    Emit(new RunEvent { Kind = RunEventKind.NodeFailed, RunId = runId, ActivationId = completed.Id, AgentId = completed.Agent.Id, AgentName = completed.Agent.Name, Text = message });
                    throw new AgentFailedException($"Agent '{completed.Agent.Name}' failed: {message}", ex);
                }

                Emit(new RunEvent { Kind = RunEventKind.NodeCompleted, RunId = runId, ActivationId = completed.Id, AgentId = completed.Agent.Id, AgentName = completed.Agent.Name, Text = result.Output });
                if (!Route(completed, result))
                    finalOutputs.Add(new RunOutput(completed.Agent.Id, completed.Agent.Name, result.Output));
                FlushJoins();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = RunStatus.Cancelled;
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            error = ex.Message;
        }
        finally
        {
            pause.PausedChanged -= PauseChanged;
            if (running.Count > 0)
            {
                runCts.Cancel();
                try { await Task.WhenAll(running.Keys); } catch { /* already reported or cancelled */ }
            }
        }

        Emit(new RunEvent
        {
            Kind = status switch { RunStatus.Completed => RunEventKind.RunCompleted, RunStatus.Cancelled => RunEventKind.RunCancelled, _ => RunEventKind.RunFailed },
            RunId = runId,
            Text = error ?? string.Join("\n\n", finalOutputs.Select(o => $"[{o.AgentName}]\n{o.Output}")),
        });
        var runResult = new RunResult(runId, status, finalOutputs, error, steps, logDir);
        options.Logger?.EndRun(runId, runResult);
        return runResult;

        string NextId() => $"a{++activationCounter}";

        Task<AgentResult> StartActivation(Activation activation)
        {
            Emit(new RunEvent
            {
                Kind = RunEventKind.NodeStarted, RunId = runId, ActivationId = activation.Id,
                AgentId = activation.Agent.Id, AgentName = activation.Agent.Name,
                Text = string.Join("\n\n", activation.Inputs.Select(i => i.Message)),
            });
            return Task.Run(async () =>
            {
                var provider = options.ProviderLookup(activation.Agent.ProviderId)
                    ?? throw new InvalidOperationException($"Provider '{activation.Agent.ProviderId}' is not configured.");
                using var client = options.ChatClientFactory.Create(provider, activation.Agent.ModelId);
                return await executor.ExecuteAsync(activation.Agent, activation.Id, activation.Inputs, client, ct,
                    listToolsInPrompt: provider.Kind == ProviderKind.Ollama);
            }, ct);
        }

        bool Route(Activation from, AgentResult result)
        {
            var fired = false;
            foreach (var arrow in graph.Outgoing(from.Agent.Id))
            {
                var target = graph.FindAgent(arrow.TargetAgentId);
                if (target is null) continue;

                if (!ArrowRouting.ShouldFire(arrow, result.Output, result.Choices, out var choiceMessage))
                {
                    Emit(new RunEvent { Kind = RunEventKind.ArrowSkipped, RunId = runId, ArrowId = arrow.Id, AgentId = from.Agent.Id, AgentName = from.Agent.Name, Text = $"{arrow.Name}: condition not met" });
                    continue;
                }

                var count = traversals.GetValueOrDefault(arrow.Id);
                if (arrow.MaxTraversals > 0 && count >= arrow.MaxTraversals)
                {
                    Emit(new RunEvent { Kind = RunEventKind.Warning, RunId = runId, ArrowId = arrow.Id, AgentId = from.Agent.Id, AgentName = from.Agent.Name, Text = $"Arrow '{arrow.Name}' reached max traversals ({arrow.MaxTraversals}); not firing." });
                    continue;
                }
                traversals[arrow.Id] = count + 1;
                fired = true;

                var rendered = ArrowRouting.Render(arrow.Prompt, result.Output, result.InputText, request.Task, from.Agent.Name, choiceMessage);
                AgentInput input = arrow.Mode == ArrowMode.Handoff
                    ? new(arrow, from.Agent.Name, string.IsNullOrWhiteSpace(rendered) ? "Continue." : rendered, result.Conversation)
                    : new(arrow, from.Agent.Name, rendered, null);

                Emit(new RunEvent { Kind = RunEventKind.ArrowFired, RunId = runId, ArrowId = arrow.Id, AgentId = from.Agent.Id, AgentName = from.Agent.Name, Text = $"{arrow.Name} → {target.Name}" });

                if (target.JoinMode == JoinMode.WaitForAll)
                {
                    if (!joinBuffers.TryGetValue(target.Id, out var buffer))
                        joinBuffers[target.Id] = buffer = [];
                    buffer.Add(input);
                }
                else
                {
                    queue.Enqueue(new Activation(NextId(), target, [input]));
                }
            }
            return fired;
        }

        // A waiting agent runs once nothing queued or running can still send it more input.
        bool FlushJoins()
        {
            var flushed = false;
            foreach (var agentId in joinBuffers.Keys.ToList())
            {
                var busy = running.Values.Concat(queue).Any(a => ArrowRouting.CanReach(graph, a.Agent.Id, agentId));
                if (busy) continue;
                var inputs = joinBuffers[agentId];
                joinBuffers.Remove(agentId);
                queue.Enqueue(new Activation(NextId(), graph.FindAgent(agentId)!, inputs));
                flushed = true;
            }
            return flushed;
        }
    }
}

public class AgentFailedException(string message, Exception inner) : Exception(message, inner);
