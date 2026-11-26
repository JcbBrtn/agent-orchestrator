using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Persistence;
using AgentOrchestrator.Core.Tools;
using Microsoft.Extensions.AI;

namespace AgentOrchestrator.Core.Engine;

/// <summary>One incoming message for an agent activation.</summary>
public record AgentInput(ArrowDefinition? Arrow, string SourceAgentName, string Message, IReadOnlyList<ChatMessage>? HandoffHistory);

public record AgentResult(string Output, string InputText, IReadOnlyList<ChatMessage> Conversation, IReadOnlyList<RouteChoice> Choices);

/// <summary>Shared state for a single run that agent executions need.</summary>
public class RunContext
{
    public required string RunId { get; init; }
    public required GraphDefinition Graph { get; init; }
    public required RunRequest Request { get; init; }
    public required AgentToolbox Toolbox { get; init; }
    public required Action<RunEvent> Emit { get; init; }
    public required PauseTokenSource Pause { get; init; }
    public required IApprovalService Approval { get; init; }
    public IUserInputService UserInput { get; init; } = new NoUserInputService();
    public required Func<string, SkillDefinition?> SkillLookup { get; init; }

    /// <summary>Keys "agentId|toolName" approved for the rest of the run.</summary>
    public ConcurrentDictionary<string, bool> ApprovedForRun { get; } = new();
}

public class AgentExecutor(RunContext run)
{
    public async Task<AgentResult> ExecuteAsync(
        AgentDefinition agent, string activationId, IReadOnlyList<AgentInput> inputs, IChatClient client, CancellationToken ct)
    {
        var choiceArrows = run.Graph.Outgoing(agent.Id).Where(a => a.Condition.Type == ConditionType.AgentChoice).ToList();
        var choices = new List<RouteChoice>();

        var messages = new List<ChatMessage> { new(ChatRole.System, BuildSystemPrompt(agent, choiceArrows)) };
        var handoff = inputs.FirstOrDefault(i => i.HandoffHistory is { Count: > 0 })?.HandoffHistory;
        if (handoff is not null)
            messages.AddRange(handoff.Where(m => m.Role != ChatRole.System));

        var inputText = inputs.Count == 1
            ? inputs[0].Message
            : string.Join("\n\n", inputs.Select(i => $"## From {i.SourceAgentName}\n{i.Message}"));
        messages.Add(new ChatMessage(ChatRole.User, inputText));

        var functions = run.Toolbox.CreateFunctions(agent.EnabledTools).ToList();
        if (choiceArrows.Count > 0)
            functions.Add(CreateRouteTool(choiceArrows, choices));
        if (agent.EnabledTools.Contains(ToolNames.AskUser, StringComparer.OrdinalIgnoreCase))
            functions.Add(CreateAskUserTool(agent, activationId));
        var byName = functions.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        var options = new ChatOptions { Temperature = agent.Temperature };
        if (functions.Count > 0)
        {
            options.Tools = [.. functions];
            options.ToolMode = ChatToolMode.Auto;
        }

        var output = "";
        var maxIterations = Math.Max(1, agent.MaxToolIterations);
        for (var iteration = 0; ; iteration++)
        {
            await run.Pause.WaitWhilePausedAsync(ct);
            ct.ThrowIfCancellationRequested();

            if (iteration >= maxIterations)
            {
                Emit(RunEventKind.Warning, agent, activationId, text: $"Reached max tool iterations ({maxIterations}); stopping.");
                break;
            }

            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
            {
                updates.Add(update);
                if (!string.IsNullOrEmpty(update.Text))
                    Emit(RunEventKind.Token, agent, activationId, text: update.Text);
            }

            var response = updates.ToChatResponse();
            messages.AddRange(response.Messages);
            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                output = response.Text;
                Emit(RunEventKind.AssistantMessage, agent, activationId, text: response.Text);
            }

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
                break;

            foreach (var call in calls)
            {
                var result = await InvokeToolAsync(agent, activationId, call, byName, ct);
                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
            }
        }

        return new AgentResult(output, inputText, messages.Skip(1).ToList(), choices);
    }

    private async Task<string> InvokeToolAsync(
        AgentDefinition agent, string activationId, FunctionCallContent call, Dictionary<string, AIFunction> byName, CancellationToken ct)
    {
        var argsJson = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>(), Json.Compact);
        Emit(RunEventKind.ToolCallRequested, agent, activationId, tool: call.Name, callId: call.CallId, text: argsJson);

        if (!byName.TryGetValue(call.Name, out var function))
        {
            var unknown = $"Error: unknown tool '{call.Name}'.";
            Emit(RunEventKind.ToolCallCompleted, agent, activationId, tool: call.Name, callId: call.CallId, text: unknown);
            return unknown;
        }

        await run.Pause.WaitWhilePausedAsync(ct);
        if (call.Name is not (ToolNames.RouteTo or ToolNames.AskUser) && !await IsApprovedAsync(agent, call, argsJson, ct))
        {
            Emit(RunEventKind.ToolCallDenied, agent, activationId, tool: call.Name, callId: call.CallId);
            return "Error: the user denied this tool call. Do not retry the same call; adjust your approach or explain what you need.";
        }

        string result;
        try
        {
            var raw = await function.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
            result = raw switch
            {
                null => "",
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
                _ => JsonSerializer.Serialize(raw, Json.Compact),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = $"Error: {ex.GetBaseException().Message}";
        }

        Emit(RunEventKind.ToolCallCompleted, agent, activationId, tool: call.Name, callId: call.CallId, text: result);
        return result;
    }

    private async Task<bool> IsApprovedAsync(AgentDefinition agent, FunctionCallContent call, string argsJson, CancellationToken ct)
    {
        var risky = AgentToolbox.IsRisky(call.Name, call.Arguments, run.Toolbox.WorkingDirectory);
        var needsAsk = agent.ApprovalMode switch
        {
            ApprovalMode.Auto => false,
            ApprovalMode.AskForRisky => risky,
            _ => true,
        };
        var key = agent.Id + "|" + call.Name;
        if (!needsAsk || run.ApprovedForRun.ContainsKey(key))
            return true;

        var decision = await run.Approval.RequestApprovalAsync(
            new ApprovalRequest(agent.Id, agent.Name, call.Name, argsJson, risky), ct);
        if (decision == ApprovalDecision.ApproveAllForRun)
            run.ApprovedForRun[key] = true;
        return decision != ApprovalDecision.Deny;
    }

    private string BuildSystemPrompt(AgentDefinition agent, IReadOnlyList<ArrowDefinition> choiceArrows)
    {
        var sb = new StringBuilder(agent.SystemPrompt.Trim());
        sb.AppendLine().AppendLine();
        sb.AppendLine($"Working directory: {run.Toolbox.WorkingDirectory}");
        sb.AppendLine($"Original task: {run.Request.Task}");
        if (!string.IsNullOrWhiteSpace(run.Request.SessionContext))
            sb.AppendLine().AppendLine("# Earlier in this session").AppendLine(run.Request.SessionContext.Trim());

        var skills = agent.SkillIds.Select(run.SkillLookup).Where(s => s is not null).ToList();
        if (skills.Count > 0)
        {
            sb.AppendLine().AppendLine("# Skills");
            foreach (var skill in skills)
                sb.AppendLine().AppendLine($"## {skill!.Name}").AppendLine(skill.Content.Trim());
        }

        if (choiceArrows.Count > 0)
        {
            sb.AppendLine().AppendLine("# Routing");
            sb.AppendLine($"When you have finished, call the `{ToolNames.RouteTo}` tool to choose where your work goes next. Options:");
            foreach (var arrow in choiceArrows)
            {
                var target = run.Graph.FindAgent(arrow.TargetAgentId)?.Name ?? "?";
                sb.AppendLine($"- `{arrow.Name}` → {target}{(string.IsNullOrWhiteSpace(arrow.Description) ? "" : ": " + arrow.Description)}");
            }
            sb.AppendLine("You may call it more than once to send work to several destinations. If you do not call it, none of these routes fire.");
        }
        return sb.ToString();
    }

    private static AIFunction CreateRouteTool(IReadOnlyList<ArrowDefinition> arrows, List<RouteChoice> choices)
    {
        var names = string.Join(", ", arrows.Select(a => a.Name));
        return AIFunctionFactory.Create(
            ([Description("Name of the route to take.")] string route, [Description("Optional message for the next agent.")] string? message = null) =>
            {
                var arrow = arrows.FirstOrDefault(a => string.Equals(a.Name, route, StringComparison.OrdinalIgnoreCase));
                if (arrow is null)
                    return $"Error: unknown route '{route}'. Valid routes: {names}";
                lock (choices) choices.Add(new RouteChoice(arrow.Name, message));
                return $"Route '{arrow.Name}' selected.";
            },
            ToolNames.RouteTo,
            $"Choose the next route for your work. Valid routes: {names}");
    }

    private AIFunction CreateAskUserTool(AgentDefinition agent, string activationId) =>
        AIFunctionFactory.Create(
            async ([Description("The question to ask the user. Be concise and specific.")] string question,
                   [Description("Optional suggested answers the user can pick from.")] string[]? options = null,
                   CancellationToken ct = default) =>
            {
                var choices = (options ?? []).Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
                var shown = choices.Count == 0 ? question : question + "\n" + string.Join("\n", choices.Select(c => "- " + c));
                Emit(RunEventKind.UserQuestion, agent, activationId, tool: ToolNames.AskUser, text: shown);
                var answer = await run.UserInput.AskAsync(new UserQuestion(agent.Id, agent.Name, question, choices), ct);
                Emit(RunEventKind.UserAnswer, agent, activationId, tool: ToolNames.AskUser, text: answer ?? "(skipped)");
                return string.IsNullOrWhiteSpace(answer)
                    ? "The user did not answer. Make a reasonable assumption, state it, and continue."
                    : "User's answer: " + answer;
            },
            ToolNames.AskUser,
            "Ask the human user a question and wait for their answer. Use when requirements are ambiguous or before risky/irreversible decisions. Ask one question at a time.");

    private void Emit(RunEventKind kind, AgentDefinition agent, string activationId, string? tool = null, string? callId = null, string? text = null) =>
        run.Emit(new RunEvent
        {
            Kind = kind,
            RunId = run.RunId,
            ActivationId = activationId,
            AgentId = agent.Id,
            AgentName = agent.Name,
            ToolName = tool,
            CallId = callId,
            Text = text,
        });
}
