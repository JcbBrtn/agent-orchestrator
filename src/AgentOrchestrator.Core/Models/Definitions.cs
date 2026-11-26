using System.Text.Json.Serialization;

namespace AgentOrchestrator.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalMode
{
    Auto,
    AskForRisky,
    AlwaysAsk,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JoinMode
{
    /// <summary>Each incoming arrow activates the agent independently.</summary>
    Any,
    /// <summary>Wait until no upstream work can still reach this agent, then run once with all inputs merged.</summary>
    WaitForAll,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArrowMode
{
    /// <summary>Target receives the arrow's prompt template rendered with the source output.</summary>
    Template,
    /// <summary>Target receives the source agent's full conversation plus the arrow's added context.</summary>
    Handoff,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConditionType
{
    Always,
    Keyword,
    Regex,
    AgentChoice,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderKind
{
    Ollama,
    LMStudio,
    /// <summary>Retired by GitHub on 2026-07-30; kept so existing settings still load.</summary>
    GitHubModels,
    OpenAICompatible,
    GitHubCopilot,
}

public static class ToolNames
{
    public const string ReadFile = "read_file";
    public const string CreateFile = "create_file";
    public const string EditFile = "edit_file";
    public const string ListDirectory = "list_directory";
    public const string SearchFiles = "search_files";
    public const string WebRequest = "web_request";
    public const string RunPowerShell = "run_powershell";
    public const string AskUser = "ask_user";
    public const string RouteTo = "route_to";

    public static readonly string[] All =
        [ReadFile, CreateFile, EditFile, ListDirectory, SearchFiles, WebRequest, RunPowerShell, AskUser];
}

public class AgentDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Agent";
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string SystemPrompt { get; set; } = "You are a helpful assistant.";
    public float? Temperature { get; set; }
    public List<string> EnabledTools { get; set; } = [ToolNames.ReadFile, ToolNames.ListDirectory, ToolNames.SearchFiles, ToolNames.AskUser];
    public List<string> SkillIds { get; set; } = [];
    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.AskForRisky;
    public JoinMode JoinMode { get; set; } = JoinMode.Any;
    public int MaxToolIterations { get; set; } = 25;
    public double X { get; set; }
    public double Y { get; set; }
}

public class ArrowCondition
{
    public ConditionType Type { get; set; } = ConditionType.Always;
    public string Value { get; set; } = "";
    public bool Negate { get; set; }
}

public class ArrowDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceAgentId { get; set; } = "";
    public string TargetAgentId { get; set; } = "";
    public string Name { get; set; } = "next";
    public string Description { get; set; } = "";
    public ArrowMode Mode { get; set; } = ArrowMode.Template;

    /// <summary>
    /// Template mode: the message sent to the target. Handoff mode: added context appended to the handed-off conversation.
    /// Placeholders: {{output}}, {{input}}, {{task}}, {{source}}, {{message}} (agent-chosen route message).
    /// </summary>
    public string Prompt { get; set; } = "{{output}}";
    public ArrowCondition Condition { get; set; } = new();
    public int MaxTraversals { get; set; } = 5;
}

public class GraphReference
{
    public string Title { get; set; } = "";
    public string Authors { get; set; } = "";
    public string Url { get; set; } = "";
}

public class GraphDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled";
    public string Description { get; set; } = "";
    public List<GraphReference> References { get; set; } = [];
    public int MaxTotalSteps { get; set; } = 50;
    public bool RunBranchesInParallel { get; set; } = true;
    public List<AgentDefinition> Agents { get; set; } = [];
    public List<ArrowDefinition> Arrows { get; set; } = [];

    public AgentDefinition? FindAgent(string id) => Agents.FirstOrDefault(a => a.Id == id);
    public IEnumerable<ArrowDefinition> Outgoing(string agentId) => Arrows.Where(a => a.SourceAgentId == agentId);
}

public class SkillDefinition
{
    /// <summary>File name without extension.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>A workspace: a working directory plus the orchestration (graph) used there, and the runs made in it.</summary>
public class SessionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New session";
    public string WorkingDirectory { get; set; } = "";
    public string GraphPath { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<string> RunIds { get; set; } = [];
}

public class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ProviderKind Kind { get; set; }
    public string Endpoint { get; set; } = "";

    /// <summary>Key into the secret store; null/empty for providers that need no key.</summary>
    public string? SecretName { get; set; }

    public static string DefaultEndpoint(ProviderKind kind) => kind switch
    {
        ProviderKind.Ollama => "http://localhost:11434/v1",
        ProviderKind.LMStudio => "http://localhost:1234/v1",
        ProviderKind.GitHubModels => "https://models.github.ai/inference",
        ProviderKind.GitHubCopilot => "",
        _ => "http://localhost:8080/v1",
    };

    public static ProviderConfig CreateCopilot() => new() { Id = "copilot", Name = "GitHub Copilot", Kind = ProviderKind.GitHubCopilot };
}

public class AppSettings
{
    public List<ProviderConfig> Providers { get; set; } = [];
    public string? LastGraphPath { get; set; }
    public string? LastWorkingDirectory { get; set; }
    public string? Theme { get; set; }
    public string? LastSessionId { get; set; }

    /// <summary>Graph files outside the graphs folder that were opened or saved, listed under Orchestrations.</summary>
    public List<string> KnownGraphPaths { get; set; } = [];

    /// <summary>Ids of built-in graphs already copied to the graphs folder, so deleting one doesn't bring it back.</summary>
    public List<string> SeededGraphs { get; set; } = [];
    public static AppSettings CreateDefault() => new()
    {
        Providers =
        [
            ProviderConfig.CreateCopilot(),
            new() { Id = "ollama", Name = "Ollama (local)", Kind = ProviderKind.Ollama, Endpoint = ProviderConfig.DefaultEndpoint(ProviderKind.Ollama) },
            new() { Id = "lmstudio", Name = "LM Studio (local)", Kind = ProviderKind.LMStudio, Endpoint = ProviderConfig.DefaultEndpoint(ProviderKind.LMStudio) },
        ],
    };
}
