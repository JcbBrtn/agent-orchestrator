using AgentOrchestrator.Core.Engine;
using AgentOrchestrator.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentOrchestrator.App.ViewModels;

public enum TranscriptKind
{
    Info,
    Input,
    Thinking,
    Assistant,
    ToolCall,
    ToolResult,
    Denied,
    Routing,
    Warning,
    Error,
    Final,
    Question,
    Answer,
}

public partial class TranscriptEntryViewModel : ObservableObject
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string? AgentId { get; init; }
    public string? ActivationId { get; init; }
    public string Header { get; init; } = "";
    public TranscriptKind Kind { get; init; }

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isExpanded = true;

    public static TranscriptEntryViewModel? FromEvent(RunEvent e)
    {
        var who = e.AgentName ?? "Run";
        if (e.ToolName == ToolNames.AskUser && e.Kind is RunEventKind.ToolCallRequested or RunEventKind.ToolCallCompleted)
            return null; // shown as UserQuestion / UserAnswer instead
        var entry = e.Kind switch
        {
            RunEventKind.UserQuestion => new() { Kind = TranscriptKind.Question, AgentId = e.AgentId, ActivationId = e.ActivationId, Header = $"{who} ❓ asks you", Text = e.Text ?? "" },
            RunEventKind.UserAnswer => new() { Kind = TranscriptKind.Answer, AgentId = e.AgentId, ActivationId = e.ActivationId, Header = "🙋 Your answer", Text = e.Text ?? "" },
            RunEventKind.RunStarted => new TranscriptEntryViewModel { Kind = TranscriptKind.Info, Header = $"▶ Run started {e.Time.LocalDateTime:g}", Text = e.Text ?? "" },
            RunEventKind.NodeStarted => new() { Kind = TranscriptKind.Input, AgentId = e.AgentId, ActivationId = e.ActivationId, Header = $"{who} ▶ input", Text = e.Text ?? "", IsExpanded = false },
            RunEventKind.Reasoning => new() { Kind = TranscriptKind.Thinking, AgentId = e.AgentId, ActivationId = e.ActivationId, Header = $"{who} 💭 thinking", Text = e.Text ?? "", IsExpanded = false },
            RunEventKind.ToolCallRequested => new() { Kind = TranscriptKind.ToolCall, AgentId = e.AgentId, ActivationId = e.ActivationId, Header = $"{who} 🔧 {e.ToolName}", Text = e.Text ?? "" },
            RunEventKind.ToolCallCompleted => new() { Kind = TranscriptKind.ToolResult, AgentId = e.AgentId, ActivationId = e.ActivationId, Header = $"{who} ← {e.ToolName} result", Text = e.Text ?? "", IsExpanded = false },
            RunEventKind.ToolCallDenied => new() { Kind = TranscriptKind.Denied, AgentId = e.AgentId, ActivationId = e.ActivationId, Header = $"{who} ✖ {e.ToolName} denied" },
            RunEventKind.ArrowFired => new() { Kind = TranscriptKind.Routing, AgentId = e.AgentId, Header = $"{who} → {e.Text}" },
            RunEventKind.Warning => new() { Kind = TranscriptKind.Warning, AgentId = e.AgentId, Header = $"⚠ {who}", Text = e.Text ?? "" },
            RunEventKind.NodeFailed => new() { Kind = TranscriptKind.Error, AgentId = e.AgentId, Header = $"{who} failed", Text = e.Text ?? "" },
            RunEventKind.Paused => new() { Kind = TranscriptKind.Info, Header = "⏸ Paused" },
            RunEventKind.Resumed => new() { Kind = TranscriptKind.Info, Header = "▶ Resumed" },
            RunEventKind.RunCompleted => new() { Kind = TranscriptKind.Final, Header = "✔ Run completed — final output", Text = e.Text ?? "" },
            RunEventKind.RunFailed => new() { Kind = TranscriptKind.Error, Header = "Run failed", Text = e.Text ?? "" },
            RunEventKind.RunCancelled => new() { Kind = TranscriptKind.Warning, Header = "Run stopped" },
            _ => null,
        };
        if (entry is not null) entry.Time = e.Time;
        return entry;
    }
}
