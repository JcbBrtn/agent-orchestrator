using System.IO;
using AgentOrchestrator.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentOrchestrator.App.ViewModels;

public partial class SessionItemViewModel(SessionDefinition model) : ObservableObject
{
    public SessionDefinition Model { get; } = model;

    [ObservableProperty] private bool _isActive;

    public string Name => Model.Name;
    public string WorkingDirectory => Model.WorkingDirectory;
    public string FolderName => Path.GetFileName(Model.WorkingDirectory.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Model.WorkingDirectory;
    public string GraphName => string.IsNullOrEmpty(Model.GraphPath) ? "(no orchestration)" : Path.GetFileNameWithoutExtension(Model.GraphPath);
    public bool FolderMissing => !Directory.Exists(Model.WorkingDirectory);

    public string Details
    {
        get
        {
            var runs = Model.RunIds.Count switch { 0 => "no runs", 1 => "1 run", var n => $"{n} runs" };
            return $"{GraphName} · {runs} · {Relative(Model.UpdatedAt)}";
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private static string Relative(DateTimeOffset time)
    {
        var age = DateTimeOffset.Now - time;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays}d ago";
        return time.LocalDateTime.ToString("d");
    }
}
