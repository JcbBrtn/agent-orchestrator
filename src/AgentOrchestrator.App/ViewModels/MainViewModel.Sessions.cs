using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AgentOrchestrator.App.Views;
using AgentOrchestrator.Core.Engine;
using AgentOrchestrator.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentOrchestrator.App.ViewModels;

/// <summary>Sessions: a working directory + orchestration pair whose runs (and transcript) are kept together.</summary>
public partial class MainViewModel
{
    private const int MaxReplayedRuns = 20;
    private SessionItemViewModel? _activeSession;

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    /// <summary>The session shown in the left panel and used for runs. Setting it opens the session.</summary>
    public SessionItemViewModel? ActiveSession
    {
        get => IsComposing ? null : _activeSession;
        set
        {
            if (value is not null && value == _activeSession)
            {
                IsComposing = false;
                return;
            }
            if (value == _activeSession) return;
            if (value is null || !OpenSession(value, startup: false))
                Dispatch(() => OnPropertyChanged(nameof(ActiveSession))); // revert the list selection
        }
    }

    public bool HasActiveSession => _activeSession is not null;

    /// <summary>The active session's details panel is hidden while the new-session page is shown.</summary>
    public bool ShowSessionDetails => HasActiveSession && !IsComposing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveSession), nameof(ShowSessionDetails), nameof(SelectedOrchestration))]
    private bool _isComposing;

    [ObservableProperty] private NewSessionViewModel? _composer;

    private void LoadSessions()
    {
        Sessions.Clear();
        foreach (var session in _services.Sessions.List())
            Sessions.Add(new SessionItemViewModel(session));
    }

    /// <summary>Loads the session's graph and transcript. Returns false if the user cancelled.</summary>
    private bool OpenSession(SessionItemViewModel item, bool startup)
    {
        if (IsRunning) return false;
        if (!startup && !ConfirmDiscard()) return false;

        var (graph, path) = LoadSessionGraph(item.Model);
        if (graph is not null)
            LoadGraph(graph, path);
        else if (!startup)
            MessageBox.Show($"The orchestration for this session could not be found:\n{item.Model.GraphPath}\n\nThe current graph is kept; use Edit session to pick another.",
                "Session", MessageBoxButton.OK, MessageBoxImage.Warning);

        SetActiveSession(item);
        IsComposing = false;
        ReplaySessionTranscript(item.Model);
        StatusText = item.FolderMissing ? $"Session folder not found: {item.WorkingDirectory}" : $"Session: {item.Name} — {item.WorkingDirectory}";
        return true;
    }

    private void SetActiveSession(SessionItemViewModel? item)
    {
        if (_activeSession is not null) _activeSession.IsActive = false;
        _activeSession = item;
        if (item is not null) item.IsActive = true;
        _services.Settings.LastSessionId = item?.Model.Id;
        _services.SaveSettings();
        OnPropertyChanged(nameof(ActiveSession));
        OnPropertyChanged(nameof(HasActiveSession));
        OnPropertyChanged(nameof(ShowSessionDetails));
        OnPropertyChanged(nameof(SelectedOrchestration));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>The session's graph file, falling back to the snapshot saved with its latest run.</summary>
    private (GraphDefinition? Graph, string? Path) LoadSessionGraph(SessionDefinition session)
    {
        if (File.Exists(session.GraphPath))
        {
            try { return (_services.Graphs.Load(session.GraphPath), session.GraphPath); }
            catch { /* try the run snapshot */ }
        }
        foreach (var runId in Enumerable.Reverse(session.RunIds))
        {
            var snapshot = Path.Combine(_services.RunLogger.RunsDirectory, runId, "graph.json");
            try
            {
                if (File.Exists(snapshot)) return (_services.Graphs.Load(snapshot), null);
            }
            catch { /* keep looking */ }
        }
        return (null, null);
    }

    private async void ReplaySessionTranscript(SessionDefinition session)
    {
        ClearTranscript();
        var generation = _replayGeneration;
        foreach (var n in Nodes) n.RunState = NodeRunState.Idle;
        var runIds = session.RunIds.TakeLast(MaxReplayedRuns).ToList();
        var hiddenRuns = session.RunIds.Count - runIds.Count;
        var logger = _services.RunLogger;
        var events = await Task.Run(() => runIds.Select(runId => (runId, logger.LoadEvents(runId))).ToList());
        if (generation != _replayGeneration) return;

        if (hiddenRuns > 0)
            AddTranscriptEntry(new TranscriptEntryViewModel { Kind = TranscriptKind.Info, Header = $"… {hiddenRuns} older run(s) hidden (see Run history)" });
        foreach (var (_, runEvents) in events)
        {
            foreach (var e in runEvents)
            {
                var entry = e.Kind == RunEventKind.AssistantMessage
                    ? new TranscriptEntryViewModel { Time = e.Time, Kind = TranscriptKind.Assistant, AgentId = e.AgentId, ActivationId = e.ActivationId, Header = $"{e.AgentName} 💬", Text = e.Text ?? "" }
                    : TranscriptEntryViewModel.FromEvent(e);
                if (entry is null) continue;
                if (entry.Kind is TranscriptKind.Input or TranscriptKind.ToolCall or TranscriptKind.ToolResult)
                    entry.IsExpanded = false;
                AddTranscriptEntry(entry);
            }
        }
    }


    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewSession() => ShowComposer();

    /// <summary>Shows the new-session page (a single prompt box with the session settings underneath).</summary>
    private void ShowComposer(string? preferredGraphPath = null, string? workingDirectory = null, string? taskText = null, string? entryName = null,
        SessionItemViewModel? rerunTarget = null)
    {
        var (choices, selected) = BuildGraphChoices(preferredGraphPath);
        Composer = new NewSessionViewModel(choices, selected,
            workingDirectory ?? _activeSession?.WorkingDirectory ?? _services.Settings.LastWorkingDirectory, AgentsOf, taskText ?? Composer?.TaskText ?? "")
        {
            RerunTarget = rerunTarget,
        };
        if (entryName is not null)
            Composer.SelectedEntry = Composer.EntryAgents.FirstOrDefault(e => e.Name == entryName) ?? Composer.SelectedEntry;
        IsComposing = true;
    }

    /// <summary>
    /// Opens the new-session page prefilled with the session's starting prompt and entry agent. Starting it reruns the
    /// task in the same session with the current orchestration; the session's previous runs are deleted only then.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RerunSession(SessionItemViewModel? item)
    {
        item ??= _activeSession;
        if (item is null) return;
        var firstRun = SessionRerun.FirstRun(_services.RunLogger, item.Model);
        if (firstRun is null)
        {
            MessageBox.Show("This session has no runs yet, so there is no starting prompt to rerun.", "Rerun session", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (item.FolderMissing)
        {
            MessageBox.Show($"The session's working directory does not exist:\n{item.WorkingDirectory}\n\nUse Edit session to choose another folder.", "Rerun session", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var runCount = item.Model.RunIds.Count;
        if (MessageBox.Show($"Rerun '{item.Name}' in this session?\n\nThis deletes its {runCount} previous run(s) and transcript, including their run logs in Run history. This cannot be undone.",
                "Rerun session", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (item != _activeSession && !OpenSession(item, startup: false)) return;

        ShowComposer(workingDirectory: item.WorkingDirectory, taskText: firstRun.Task, entryName: firstRun.EntryAgentName, rerunTarget: item);
        StatusText = "Rerun prepared: edit the task if needed, then start. Previous runs are deleted when you start.";
    }

    [RelayCommand]
    private void CancelComposer() => IsComposing = false;

    /// <summary>Agents of a graph choice, the likely entry point (an agent nothing points to) first.</summary>
    private IReadOnlyList<EntryChoice> AgentsOf(GraphChoice choice)
    {
        GraphDefinition graph;
        if (choice.Path is null || string.Equals(choice.Path, FilePath, StringComparison.OrdinalIgnoreCase))
            graph = Graph;
        else
        {
            try { graph = _services.Graphs.Load(choice.Path); }
            catch { return []; }
        }
        var targets = graph.Arrows.Select(a => a.TargetAgentId).ToHashSet();
        return graph.Agents.OrderBy(a => targets.Contains(a.Id) ? 1 : 0)
            .Select(a => new EntryChoice(a.Id, a.Name)).ToList();
    }

    /// <summary>Creates a session from the new-session page and immediately runs its task.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task StartNewSessionAsync()
    {
        var c = Composer;
        if (c is null) return;
        var task = c.TaskText.Trim();
        if (task.Length == 0)
        {
            MessageBox.Show("Describe what the agents should do.", "New session");
            return;
        }
        if (!Directory.Exists(c.WorkingDirectory))
        {
            MessageBox.Show("Choose an existing working directory.", "New session");
            c.BrowseFolderCommand.Execute(null);
            return;
        }
        if (c.SelectedGraph is null)
        {
            MessageBox.Show("Choose an orchestration.", "New session");
            return;
        }
        if (ResolveGraphChoice(c.SelectedGraph) is not { } graphPath)
            return;
        if (!string.Equals(graphPath, FilePath, StringComparison.OrdinalIgnoreCase))
        {
            if (!ConfirmDiscard()) return;
            try { LoadGraph(_services.Graphs.Load(graphPath), graphPath); }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open the orchestration:\n{ex.Message}", "New session", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        var entry = Nodes.FirstOrDefault(n => n.Id == c.SelectedEntry?.Id) ?? Nodes.FirstOrDefault();
        if (!ValidateAgents() || entry is null)
            return;

        var session = c.RerunTarget is { } rerunTarget
            ? PrepareRerun(rerunTarget, graphPath)
            : AddSession(SessionNameFromTask(task), c.WorkingDirectory, graphPath);
        _services.Settings.LastWorkingDirectory = c.WorkingDirectory;
        c.TaskText = "";
        IsComposing = false;
        await ExecuteRunAsync(session, entry.Id, task);
    }

    /// <summary>Deletes a session's previous runs and transcript so its rerun starts fresh, and returns the session.</summary>
    private SessionItemViewModel PrepareRerun(SessionItemViewModel item, string graphPath)
    {
        SessionRerun.DeleteRuns(_services.RunLogger, item.Model);
        item.Model.GraphPath = graphPath;
        item.Model.UpdatedAt = DateTimeOffset.Now;
        _services.Sessions.Save(item.Model);
        item.Refresh();
        SetActiveSession(item);
        ClearTranscript();
        return item;
    }

    /// <summary>Creates, lists and activates a new session (with an empty transcript).</summary>
    private SessionItemViewModel AddSession(string name, string workingDirectory, string graphPath)
    {
        var session = new SessionDefinition { Name = name, WorkingDirectory = workingDirectory, GraphPath = graphPath };
        _services.Sessions.Save(session);
        var item = new SessionItemViewModel(session);
        Sessions.Insert(0, item);
        SetActiveSession(item);
        ClearTranscript();
        return item;
    }

    private static string SessionNameFromTask(string task)
    {
        var line = task.Split('\n')[0].Trim();
        return line.Length <= 48 ? line : line[..47].TrimEnd() + "…";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void EditSession(SessionItemViewModel? item)
    {
        item ??= _activeSession;
        if (item is null) return;
        var (choices, selected) = BuildGraphChoices(item.Model.GraphPath);
        var dialog = new SessionDialog(choices, selected, item.WorkingDirectory, item.Name, isEdit: true) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || ResolveGraphChoice(dialog.Graph!) is not { } graphPath)
            return;

        var graphChanged = !string.Equals(graphPath, item.Model.GraphPath, StringComparison.OrdinalIgnoreCase);
        item.Model.Name = dialog.SessionName;
        item.Model.WorkingDirectory = dialog.WorkingDirectory;
        item.Model.GraphPath = graphPath;
        _services.Sessions.Save(item.Model);
        item.Refresh();

        if (item == _activeSession && graphChanged && !string.Equals(graphPath, FilePath, StringComparison.OrdinalIgnoreCase) && ConfirmDiscard())
        {
            var (graph, path) = LoadSessionGraph(item.Model);
            if (graph is not null) LoadGraph(graph, path);
        }
        OnPropertyChanged(nameof(Title));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DeleteSession(SessionItemViewModel? item)
    {
        if (item is null) return;
        if (MessageBox.Show($"Delete session '{item.Name}'?\n\nFiles in the working directory and run logs (Run history) are not deleted.",
                "Delete session", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _services.Sessions.Delete(item.Model.Id);
        Sessions.Remove(item);
        if (item == _activeSession)
        {
            SetActiveSession(null);
            ClearTranscript();
            ShowComposer();
        }
    }

    [RelayCommand]
    private void OpenSessionFolder(SessionItemViewModel? item)
    {
        item ??= _activeSession;
        if (item is null || item.FolderMissing) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.WorkingDirectory}\"") { UseShellExecute = true });
    }

    /// <summary>Saved graphs plus the graph currently open (which may be unsaved).</summary>
    private (List<GraphChoice> Choices, GraphChoice? Selected) BuildGraphChoices(string? preferredPath)
    {
        var choices = new List<GraphChoice>();
        if (FilePath is null)
            choices.Add(new GraphChoice($"Current graph: {Graph.Name} (unsaved, you will be asked to save it)", null));

        var files = _services.Graphs.List().Concat(_services.Settings.KnownGraphPaths)
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (FilePath is not null && !files.Contains(FilePath, StringComparer.OrdinalIgnoreCase)) files.Insert(0, FilePath);
        if (preferredPath is not null && File.Exists(preferredPath) && !files.Contains(preferredPath, StringComparer.OrdinalIgnoreCase)) files.Insert(0, preferredPath);

        var graphsDir = _services.Paths.GraphsDir;
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var inGraphsDir = string.Equals(Path.GetDirectoryName(file), graphsDir, StringComparison.OrdinalIgnoreCase);
            var open = string.Equals(file, FilePath, StringComparison.OrdinalIgnoreCase) ? "  (open)" : "";
            choices.Add(new GraphChoice(inGraphsDir ? name + open : $"{name}{open}  —  {file}", file));
        }

        var want = preferredPath ?? FilePath;
        var selected = choices.FirstOrDefault(c => want is not null && string.Equals(c.Path, want, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
        return (choices, selected);
    }

    /// <summary>Returns the graph file path for a choice, saving the current graph first if needed (null if cancelled).</summary>
    private string? ResolveGraphChoice(GraphChoice choice)
    {
        if (choice.Path is not null) return choice.Path;
        return TrySave() ? FilePath : null;
    }

    private void RecordSessionRun(SessionItemViewModel item, string runId)
    {
        item.Model.RunIds.Add(runId);
        item.Model.UpdatedAt = DateTimeOffset.Now;
        if (FilePath is not null) item.Model.GraphPath = FilePath;
        _services.Sessions.Save(item.Model);
        item.Refresh();
        var index = Sessions.IndexOf(item);
        if (index > 0) Sessions.Move(index, 0);
    }
}
