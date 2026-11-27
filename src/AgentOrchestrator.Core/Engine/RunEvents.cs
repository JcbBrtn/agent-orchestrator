using System.Text.Json.Serialization;

namespace AgentOrchestrator.Core.Engine;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunEventKind
{
    RunStarted,
    NodeStarted,
    Token,
    ReasoningToken,
    Reasoning,
    AssistantMessage,
    ToolCallRequested,
    ToolCallDenied,
    ToolCallCompleted,
    UserQuestion,
    UserAnswer,
    ArrowFired,
    ArrowSkipped,
    NodeCompleted,
    NodeFailed,
    Warning,
    Paused,
    Resumed,
    RunCompleted,
    RunFailed,
    RunCancelled,
}

public record RunEvent
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public RunEventKind Kind { get; init; }
    public string RunId { get; init; } = "";
    public string? ActivationId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentName { get; init; }
    public string? ArrowId { get; init; }
    public string? ToolName { get; init; }
    public string? CallId { get; init; }
    public string? Text { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    Completed,
    Failed,
    Cancelled,
}

public record RunOutput(string AgentId, string AgentName, string Output);

public record RunResult(string RunId, RunStatus Status, IReadOnlyList<RunOutput> FinalOutputs, string? Error, int Steps, string? LogDirectory);

/// <param name="SessionContext">Optional summary of earlier runs in the same session, added to every agent's system prompt.</param>
public record RunRequest(string EntryAgentId, string Task, string WorkingDirectory, string? SessionContext = null);
