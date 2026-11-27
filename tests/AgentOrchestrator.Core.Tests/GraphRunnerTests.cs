using AgentOrchestrator.Core.Engine;
using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Tools;
using Microsoft.Extensions.AI;

namespace AgentOrchestrator.Core.Tests;

public class GraphRunnerTests : IDisposable
{
    private readonly FakeFactory _factory = new();
    private readonly string _workDir = Directory.CreateTempSubdirectory("ao-tests").FullName;
    private readonly List<RunEvent> _events = [];
    private static readonly ProviderConfig Provider = new() { Id = "fake", Name = "Fake" };

    public void Dispose() => Directory.Delete(_workDir, recursive: true);

    private static AgentDefinition Agent(string name, ApprovalMode approval = ApprovalMode.Auto, JoinMode join = JoinMode.Any) => new()
    {
        Id = name, Name = name, ProviderId = "fake", ModelId = name, EnabledTools = [], ApprovalMode = approval, JoinMode = join,
    };

    private static ArrowDefinition Arrow(string from, string to, string name = "next", ConditionType type = ConditionType.Always,
        string value = "", bool negate = false, ArrowMode mode = ArrowMode.Template, string prompt = "{{output}}", int max = 5) => new()
    {
        SourceAgentId = from, TargetAgentId = to, Name = name, Mode = mode, Prompt = prompt, MaxTraversals = max,
        Condition = new ArrowCondition { Type = type, Value = value, Negate = negate },
    };

    private Task<RunResult> Run(GraphDefinition graph, string entry, IApprovalService? approval = null,
        IUserInputService? userInput = null, string? sessionContext = null) =>
        new GraphRunner(new GraphRunnerOptions
        {
            ChatClientFactory = _factory,
            ProviderLookup = _ => Provider,
            Approval = approval ?? new AutoApproveService(),
            UserInput = userInput ?? new NoUserInputService(),
        }).RunAsync(graph, new RunRequest(entry, "the task", _workDir, sessionContext), e => { lock (_events) _events.Add(e); });

    private static string LastUser(IList<ChatMessage> m) => m.Last(x => x.Role == ChatRole.User).Text;

    [Fact]
    public async Task Template_arrow_renders_placeholders_into_target_input()
    {
        var graph = new GraphDefinition { Agents = [Agent("A"), Agent("B")], Arrows = [Arrow("A", "B", prompt: "Review ({{source}}, {{task}}): {{output}}")] };
        _factory.Responders["A"] = _ => FakeChatClient.Text("hello");
        _factory.Responders["B"] = m => FakeChatClient.Text("got " + LastUser(m));

        var result = await Run(graph, "A");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("got Review (A, the task): hello", Assert.Single(result.FinalOutputs).Output);
    }

    [Fact]
    public async Task Loop_memory_replays_only_the_agents_own_messages_on_a_loop_back()
    {
        var a = Agent("A");
        a.KeepLoopMemory = true;
        var graph = new GraphDefinition { Agents = [a, Agent("B")], Arrows = [Arrow("A", "B"), Arrow("B", "A", prompt: "from-B", max: 1)] };
        var n = 0;
        _factory.Responders["A"] = _ => FakeChatClient.Text($"a{++n}");
        _factory.Responders["B"] = _ => FakeChatClient.Text("B-SECRET");

        await Run(graph, "A");

        var second = _factory.Calls["A"][1];
        Assert.Equal(4, second.Count);
        Assert.Equal(ChatRole.System, second[0].Role);
        Assert.Equal("the task", second[1].Text);
        Assert.Equal("a1", second[2].Text);
        Assert.Equal("from-B", second[3].Text);
        Assert.DoesNotContain(second, m => m.Text.Contains("B-SECRET"));
    }

    [Fact]
    public async Task Loop_without_memory_flag_starts_fresh_each_pass()
    {
        var graph = new GraphDefinition { Agents = [Agent("A"), Agent("B")], Arrows = [Arrow("A", "B"), Arrow("B", "A", prompt: "from-B", max: 1)] };
        _factory.Responders["A"] = _ => FakeChatClient.Text("a");
        _factory.Responders["B"] = _ => FakeChatClient.Text("b");

        await Run(graph, "A");

        var second = _factory.Calls["A"][1];
        Assert.Equal(2, second.Count);
        Assert.Equal("from-B", second[1].Text);
    }

    [Fact]
    public async Task Loop_memory_drops_oldest_messages_beyond_the_cap()
    {
        var a = Agent("A");
        a.KeepLoopMemory = true;
        a.MemoryMaxMessages = 2;
        var graph = new GraphDefinition { Agents = [a, Agent("B")], Arrows = [Arrow("A", "B"), Arrow("B", "A", prompt: "from-B", max: 2)] };
        var n = 0;
        _factory.Responders["A"] = _ => FakeChatClient.Text($"a{++n}");
        _factory.Responders["B"] = _ => FakeChatClient.Text("b");

        await Run(graph, "A");

        var third = _factory.Calls["A"][2].Skip(1).Select(m => m.Text).ToList();
        Assert.Equal(["from-B", "a2", "from-B"], third);
    }

    [Fact]
    public async Task Ollama_system_prompt_lists_enabled_tools()
    {
        var agent = Agent("A");
        agent.EnabledTools = [ToolNames.ReadFile];
        var graph = new GraphDefinition { Agents = [agent] };
        string? system = null;
        _factory.Responders["A"] = m => { system = m[0].Text; return FakeChatClient.Text("ok"); };

        await RunWithProvider(graph, "A", ProviderKind.Ollama);

        Assert.Contains($"- {ToolNames.ReadFile}:", system);
    }

    [Fact]
    public async Task Non_ollama_system_prompt_does_not_list_tools()
    {
        var agent = Agent("A");
        agent.EnabledTools = [ToolNames.ReadFile];
        var graph = new GraphDefinition { Agents = [agent] };
        string? system = null;
        _factory.Responders["A"] = m => { system = m[0].Text; return FakeChatClient.Text("ok"); };

        await RunWithProvider(graph, "A", ProviderKind.LMStudio);

        Assert.DoesNotContain("# Tools", system);
    }

    private Task<RunResult> RunWithProvider(GraphDefinition graph, string entry, ProviderKind kind) =>
        new GraphRunner(new GraphRunnerOptions
        {
            ChatClientFactory = _factory,
            ProviderLookup = _ => new ProviderConfig { Id = "fake", Name = "Fake", Kind = kind },
            Approval = new AutoApproveService(),
            UserInput = new NoUserInputService(),
        }).RunAsync(graph, new RunRequest(entry, "the task", _workDir, null), e => { lock (_events) _events.Add(e); });

    [Fact]
    public async Task Keyword_loop_runs_until_reviewer_approves()
    {
        var graph = new GraphDefinition
        {
            Agents = [Agent("coder"), Agent("reviewer")],
            Arrows = [Arrow("coder", "reviewer"), Arrow("reviewer", "coder", "rework", ConditionType.Keyword, "APPROVED", negate: true)],
        };
        var reviews = 0;
        _factory.Responders["coder"] = _ => FakeChatClient.Text("code");
        _factory.Responders["reviewer"] = _ => FakeChatClient.Text(++reviews < 3 ? "needs changes" : "APPROVED");

        var result = await Run(graph, "coder");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(3, _factory.CallCount("coder"));
        Assert.Equal("APPROVED", Assert.Single(result.FinalOutputs).Output);
    }

    [Fact]
    public async Task Loop_stops_at_arrow_max_traversals()
    {
        var graph = new GraphDefinition
        {
            Agents = [Agent("coder"), Agent("reviewer")],
            Arrows = [Arrow("coder", "reviewer", max: 10), Arrow("reviewer", "coder", "rework", max: 2)],
        };
        _factory.Responders["coder"] = _ => FakeChatClient.Text("code");
        _factory.Responders["reviewer"] = _ => FakeChatClient.Text("never happy");

        var result = await Run(graph, "coder");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(3, _factory.CallCount("coder"));
        Assert.Contains(_events, e => e.Kind == RunEventKind.Warning && e.Text!.Contains("max traversals"));
        Assert.Contains("[Stopped: arrow 'rework' reached max traversals (2)]", Assert.Single(result.FinalOutputs).Output);
    }

    [Fact]
    public async Task Agent_choice_fires_only_chosen_arrow_with_message()
    {
        var graph = new GraphDefinition
        {
            Agents = [Agent("router"), Agent("left"), Agent("right")],
            Arrows =
            [
                Arrow("router", "left", "go_left", ConditionType.AgentChoice, prompt: "{{message}}"),
                Arrow("router", "right", "go_right", ConditionType.AgentChoice, prompt: "{{message}}"),
            ],
        };
        _factory.Responders["router"] = m => m.Any(x => x.Role == ChatRole.Tool)
            ? FakeChatClient.Text("done routing")
            : FakeChatClient.Call(ToolNames.RouteTo, new() { ["route"] = "go_right", ["message"] = "handle this" });
        _factory.Responders["left"] = _ => FakeChatClient.Text("left");
        _factory.Responders["right"] = m => FakeChatClient.Text("right: " + LastUser(m));

        var result = await Run(graph, "router");

        Assert.Equal(0, _factory.CallCount("left"));
        Assert.Equal("right: handle this", Assert.Single(result.FinalOutputs).Output);
        Assert.Contains("go_right", _factory.Calls["router"][0].First(m => m.Role == ChatRole.System).Text);
    }

    [Fact]
    public async Task Handoff_arrow_passes_conversation_and_added_context()
    {
        var graph = new GraphDefinition
        {
            Agents = [Agent("A"), Agent("B")],
            Arrows = [Arrow("A", "B", mode: ArrowMode.Handoff, prompt: "Now continue as B")],
        };
        _factory.Responders["A"] = _ => FakeChatClient.Text("A's answer");
        _factory.Responders["B"] = _ => FakeChatClient.Text("B done");

        await Run(graph, "A");

        var bMessages = _factory.Calls["B"][0];
        Assert.Single(bMessages, m => m.Role == ChatRole.System);
        Assert.Contains(bMessages, m => m.Role == ChatRole.User && m.Text == "the task");
        Assert.Contains(bMessages, m => m.Role == ChatRole.Assistant && m.Text == "A's answer");
        Assert.Equal("Now continue as B", bMessages[^1].Text);
    }

    [Fact]
    public async Task WaitForAll_join_runs_once_with_all_branch_inputs()
    {
        var graph = new GraphDefinition
        {
            Agents = [Agent("start"), Agent("b1"), Agent("b2"), Agent("join", join: JoinMode.WaitForAll)],
            Arrows = [Arrow("start", "b1"), Arrow("start", "b2"), Arrow("b1", "join"), Arrow("b2", "join")],
        };
        _factory.Responders["start"] = _ => FakeChatClient.Text("go");
        _factory.Responders["b1"] = _ => FakeChatClient.Text("one");
        _factory.Responders["b2"] = _ => { Thread.Sleep(50); return FakeChatClient.Text("two"); };
        _factory.Responders["join"] = m => FakeChatClient.Text(LastUser(m));

        var result = await Run(graph, "start");

        Assert.Equal(1, _factory.CallCount("join"));
        var output = Assert.Single(result.FinalOutputs).Output;
        Assert.Contains("## From b1\none", output);
        Assert.Contains("## From b2\ntwo", output);
    }

    [Fact]
    public async Task Denied_tool_call_is_not_executed()
    {
        var writer = Agent("writer", ApprovalMode.AlwaysAsk);
        writer.EnabledTools = [ToolNames.CreateFile];
        var graph = new GraphDefinition { Agents = [writer] };
        _factory.Responders["writer"] = m => m.Any(x => x.Role == ChatRole.Tool)
            ? FakeChatClient.Text("gave up")
            : FakeChatClient.Call(ToolNames.CreateFile, new() { ["path"] = "out.txt", ["content"] = "x" });

        var result = await Run(graph, "writer", new DenyAll());

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.False(File.Exists(Path.Combine(_workDir, "out.txt")));
        Assert.Contains(_events, e => e.Kind == RunEventKind.ToolCallDenied);
    }

    [Fact]
    public async Task Approved_tool_call_executes()
    {
        var writer = Agent("writer", ApprovalMode.AlwaysAsk);
        writer.EnabledTools = [ToolNames.CreateFile];
        var graph = new GraphDefinition { Agents = [writer] };
        _factory.Responders["writer"] = m => m.Any(x => x.Role == ChatRole.Tool)
            ? FakeChatClient.Text("written")
            : FakeChatClient.Call(ToolNames.CreateFile, new() { ["path"] = "out.txt", ["content"] = "hi" });

        await Run(graph, "writer", new AutoApproveService());

        Assert.Equal("hi", File.ReadAllText(Path.Combine(_workDir, "out.txt")));
    }

    [Fact]
    public async Task Agent_failure_fails_run()
    {
        var graph = new GraphDefinition { Agents = [Agent("A")] };
        _factory.Responders["A"] = _ => throw new InvalidOperationException("boom");

        var result = await Run(graph, "A");

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task Ask_user_returns_answer_without_approval()
    {
        var agent = Agent("asker", ApprovalMode.AlwaysAsk);
        agent.EnabledTools = [ToolNames.AskUser];
        var graph = new GraphDefinition { Agents = [agent] };
        _factory.Responders["asker"] = m => m.LastOrDefault(x => x.Role == ChatRole.Tool) is { } tool
            ? FakeChatClient.Text("done: " + tool.Contents.OfType<FunctionResultContent>().Single().Result)
            : FakeChatClient.Call(ToolNames.AskUser, new() { ["question"] = "Which db?", ["options"] = new[] { "SQL", "Mongo" } });
        var input = new ScriptedInput("SQL");

        var result = await Run(graph, "asker", new DenyAll(), input);

        Assert.Equal("done: User's answer: SQL", Assert.Single(result.FinalOutputs).Output);
        var asked = Assert.Single(input.Questions);
        Assert.Equal("Which db?", asked.Question);
        Assert.Equal(["SQL", "Mongo"], asked.Options);
        Assert.DoesNotContain(_events, e => e.Kind == RunEventKind.ToolCallDenied);
        Assert.Contains(_events, e => e.Kind == RunEventKind.UserAnswer && e.Text == "SQL");
    }

    [Fact]
    public async Task Skipped_question_tells_agent_to_assume()
    {
        var agent = Agent("asker");
        agent.EnabledTools = [ToolNames.AskUser];
        _factory.Responders["asker"] = m => m.LastOrDefault(x => x.Role == ChatRole.Tool) is { } tool
            ? FakeChatClient.Text(tool.Contents.OfType<FunctionResultContent>().Single().Result!.ToString()!)
            : FakeChatClient.Call(ToolNames.AskUser, new() { ["question"] = "?" });

        var result = await Run(new GraphDefinition { Agents = [agent] }, "asker");

        Assert.Contains("did not answer", Assert.Single(result.FinalOutputs).Output);
    }

    [Fact]
    public async Task Session_context_is_added_to_system_prompt()
    {
        _factory.Responders["A"] = _ => FakeChatClient.Text("ok");

        await Run(new GraphDefinition { Agents = [Agent("A")] }, "A", sessionContext: "previously built the parser");

        var system = _factory.Calls["A"][0][0];
        Assert.Equal(ChatRole.System, system.Role);
        Assert.Contains("# Earlier in this session", system.Text);
        Assert.Contains("previously built the parser", system.Text);
    }

    private sealed class ScriptedInput(string? answer) : IUserInputService
    {
        public List<UserQuestion> Questions { get; } = [];

        public Task<string?> AskAsync(UserQuestion question, CancellationToken cancellationToken)
        {
            Questions.Add(question);
            return Task.FromResult(answer);
        }
    }

    private sealed class DenyAll : IApprovalService
    {
        public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalDecision.Deny);
    }
}
