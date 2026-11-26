using System.Collections.ObjectModel;
using System.IO;
using AgentOrchestrator.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AgentOrchestrator.App.ViewModels;

/// <summary>An agent the run can start at.</summary>
public sealed record EntryChoice(string Id, string Name);

/// <summary>The "new session" page: one prompt box plus the working directory, orchestration and entry agent underneath.</summary>
public partial class NewSessionViewModel : ObservableObject
{
    private readonly Func<GraphChoice, IReadOnlyList<EntryChoice>> _agentsOf;

    public NewSessionViewModel(IEnumerable<GraphChoice> graphs, GraphChoice? selected, string? workingDirectory,
        Func<GraphChoice, IReadOnlyList<EntryChoice>> agentsOf, string taskText = "")
    {
        _agentsOf = agentsOf;
        foreach (var g in graphs) Graphs.Add(g);
        _workingDirectory = workingDirectory ?? "";
        _taskText = taskText;
        SelectedGraph = selected ?? Graphs.FirstOrDefault();
    }

    public ObservableCollection<GraphChoice> Graphs { get; } = [];

    [ObservableProperty] private string _taskText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderDisplay))]
    private string _workingDirectory = "";

    [ObservableProperty] private GraphChoice? _selectedGraph;
    [ObservableProperty] private IReadOnlyList<EntryChoice> _entryAgents = [];
    [ObservableProperty] private EntryChoice? _selectedEntry;

    public string FolderDisplay => string.IsNullOrWhiteSpace(WorkingDirectory) ? "Choose a working directory…" : WorkingDirectory;

    partial void OnSelectedGraphChanged(GraphChoice? value)
    {
        EntryAgents = value is null ? [] : _agentsOf(value);
        SelectedEntry = EntryAgents.FirstOrDefault();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the session's working directory",
            InitialDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : null,
        };
        if (dialog.ShowDialog() == true)
            WorkingDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseGraph()
    {
        var dialog = new OpenFileDialog { Title = "Choose an orchestration", Filter = "Agent graph (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        var choice = Graphs.FirstOrDefault(g => string.Equals(g.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            choice = new GraphChoice(Path.GetFileNameWithoutExtension(dialog.FileName) + "  —  " + dialog.FileName, dialog.FileName);
            Graphs.Add(choice);
        }
        SelectedGraph = choice;
    }
}
