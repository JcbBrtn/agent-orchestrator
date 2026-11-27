using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using AgentOrchestrator.App.Services;
using AgentOrchestrator.App.Views;
using AgentOrchestrator.Core.Engine;
using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AgentOrchestrator.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Instance;
    private readonly ConcurrentQueue<RunEvent> _pendingEvents = new();
    private readonly DispatcherTimer _eventPump;
    private readonly Dictionary<string, TranscriptEntryViewModel> _streaming = [];
    private readonly Dictionary<TranscriptEntryViewModel, StringBuilder> _tokenBuffers = [];
    private const int MaxLiveTranscriptEntries = 2000;
    private const int TranscriptTrimChunk = 200;
    private TranscriptEntryViewModel? _trimNotice;
    private int _trimmedEntries;
    private int _replayGeneration;
    private CancellationTokenSource? _runCts;
    private PauseTokenSource? _pause;
    private string _savedJson = "";

    public MainViewModel()
    {
        PendingConnection = new PendingConnectionViewModel(this);
        TranscriptView = CollectionViewSource.GetDefaultView(Transcript);
        TranscriptView.Filter = o => !ShowSelectedAgentOnly || SelectedNode is null
            || (o is TranscriptEntryViewModel t && (t.AgentId is null || t.AgentId == SelectedNode.Id));

        _eventPump = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => PumpEvents(), Dispatcher.CurrentDispatcher);
        _services.Approval.WaitingForApproval += (_, r) => Dispatch(() => SetNodeState(r.AgentId, NodeRunState.WaitingApproval));
        _services.Approval.ApprovalFinished += (_, r) => Dispatch(() =>
        {
            if (IsRunning) SetNodeState(r.AgentId, NodeRunState.Running);
        });
        _services.UserInput.WaitingForUser += (_, q) => Dispatch(() => SetNodeState(q.AgentId, NodeRunState.WaitingInput));
        _services.UserInput.UserAnswered += (_, q) => Dispatch(() =>
        {
            if (IsRunning) SetNodeState(q.AgentId, NodeRunState.Running);
        });
        _services.ProvidersChanged += (_, _) => { foreach (var n in Nodes) n.RefreshProviders(); };
        _services.SkillsChanged += (_, _) => { foreach (var n in Nodes) n.RefreshSkills(); };

        LoadSessions();
        foreach (var s in Sessions.Where(s => File.Exists(s.Model.GraphPath)))
            RememberGraph(s.Model.GraphPath);
        if (Sessions.FirstOrDefault(s => s.Model.Id == _services.Settings.LastSessionId) is { } lastSession)
        {
            LoadGraph(CreateDefaultGraph(), null);
            OpenSession(lastSession, startup: true);
            return;
        }

        if (_services.Settings.LastGraphPath is { } last && File.Exists(last))
        {
            try { LoadGraph(_services.Graphs.Load(last), last); ShowComposer(); return; }
            catch { /* fall through to a new graph */ }
        }
        LoadGraph(CreateDefaultGraph(), null);
        ShowComposer();
    }

    public GraphDefinition Graph { get; private set; } = new();
    public ObservableCollection<AgentNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<ArrowViewModel> Connections { get; } = [];
    public PendingConnectionViewModel PendingConnection { get; }
    public ObservableCollection<TranscriptEntryViewModel> Transcript { get; } = [];
    public ICollectionView TranscriptView { get; }

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _showSelectedAgentOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Inspector))]
    private AgentNodeViewModel? _selectedNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Inspector))]
    private ArrowViewModel? _selectedConnection;

    /// <summary>What the side panel edits: the selected arrow, the selected agent, or the graph settings.</summary>
    public object Inspector => (object?)SelectedConnection ?? (object?)SelectedNode ?? new GraphSettingsViewModel(this);

    public string Title => $"Agent Orchestrator — {(_activeSession is null ? "" : _activeSession.Name + " — ")}{Graph.Name}{(FilePath is null ? "" : $" ({Path.GetFileName(FilePath)})")}";

    public string GraphName
    {
        get => Graph.Name;
        set { if (SetProperty(Graph.Name, value, Graph, (g, v) => g.Name = v)) OnPropertyChanged(nameof(Title)); }
    }

    public int MaxTotalSteps
    {
        get => Graph.MaxTotalSteps;
        set => SetProperty(Graph.MaxTotalSteps, value, Graph, (g, v) => g.MaxTotalSteps = v);
    }

    public bool RunBranchesInParallel
    {
        get => Graph.RunBranchesInParallel;
        set => SetProperty(Graph.RunBranchesInParallel, value, Graph, (g, v) => g.RunBranchesInParallel = v);
    }

    public bool IsDirty => JsonSerializer.Serialize(Graph, Json.Options) != _savedJson;

    public IReadOnlyList<string> ThemeNames { get; } = ThemeManager.Themes.Select(t => t.Name).ToList();

    public string SelectedTheme
    {
        get => ThemeManager.Current.Name;
        set
        {
            if (value is null || value == ThemeManager.Current.Name) return;
            ThemeManager.Apply(value);
            _services.Settings.Theme = ThemeManager.Current.Name;
            _services.SaveSettings();
            OnPropertyChanged();
        }
    }

    partial void OnSelectedNodeChanged(AgentNodeViewModel? value)
    {
        if (value is not null) SelectedConnection = null;
        TranscriptView.Refresh();
    }

    partial void OnSelectedConnectionChanged(ArrowViewModel? value)
    {
        if (value is not null) SelectedNode = null;
    }

    partial void OnShowSelectedAgentOnlyChanged(bool value) => TranscriptView.Refresh();

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(Title));

    partial void OnIsRunningChanged(bool value) => NotifyRunCommands();

    partial void OnIsPausedChanged(bool value) => NotifyRunCommands();

    // ---------- Graph editing ----------

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewGraph()
    {
        if (!ConfirmDiscard()) return;
        LoadGraph(CreateDefaultGraph(), null);
        IsComposing = false;
        LeaveSession();
    }

    private GraphDefinition CreateDefaultGraph()
    {
        var provider = _services.Settings.Providers.FirstOrDefault();
        var graph = new GraphDefinition { Name = "New graph" };
        graph.Agents.Add(new AgentDefinition { Name = "Agent 1", ProviderId = provider?.Id ?? "", X = 100, Y = 150 });
        return graph;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Open()
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = "Agent graph (*.json)|*.json", InitialDirectory = _services.Paths.GraphsDir };
        if (dialog.ShowDialog() != true) return;
        try
        {
            LoadGraph(_services.Graphs.Load(dialog.FileName), dialog.FileName);
            IsComposing = false;
            LeaveSession();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open graph:\n{ex.Message}", "Open", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Save() => TrySave();

    public bool TrySave() => FilePath is null ? SaveAsCore() : SaveTo(FilePath);

    [RelayCommand]
    private void SaveAs() => SaveAsCore();

    private bool SaveAsCore()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Agent graph (*.json)|*.json",
            InitialDirectory = _services.Paths.GraphsDir,
            FileName = string.Join("_", Graph.Name.Split(Path.GetInvalidFileNameChars())) + ".json",
        };
        return dialog.ShowDialog() == true && SaveTo(dialog.FileName);
    }

    private bool SaveTo(string path)
    {
        try
        {
            _services.Graphs.Save(path, Graph);
            _savedJson = JsonSerializer.Serialize(Graph, Json.Options);
            FilePath = path;
            _services.Settings.LastGraphPath = path;
            _services.SaveSettings();
            StatusText = $"Saved {path}";
            RememberGraph(path);
            RefreshOrchestrations();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save graph:\n{ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Returns false if the user cancelled.</summary>
    public bool ConfirmDiscard()
    {
        if (!IsDirty) return true;
        var answer = MessageBox.Show("Save changes to the current graph?", "Agent Orchestrator", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => TrySave(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void LoadGraph(GraphDefinition graph, string? path)
    {
        Graph = graph;
        SelectedNode = null;
        SelectedConnection = null;
        Connections.Clear();
        Nodes.Clear();
        foreach (var agent in graph.Agents)
            Nodes.Add(CreateNode(agent));
        foreach (var arrow in graph.Arrows.ToList())
        {
            var source = Nodes.FirstOrDefault(n => n.Id == arrow.SourceAgentId);
            var target = Nodes.FirstOrDefault(n => n.Id == arrow.TargetAgentId);
            if (source is null || target is null)
            {
                graph.Arrows.Remove(arrow);
                continue;
            }
            Connections.Add(new ArrowViewModel(arrow, source.Output, target.Input));
        }
        FilePath = path;
        _savedJson = JsonSerializer.Serialize(Graph, Json.Options);
        OnPropertyChanged(nameof(GraphName));
        OnPropertyChanged(nameof(MaxTotalSteps));
        OnPropertyChanged(nameof(RunBranchesInParallel));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Inspector));
        if (path is not null) RememberGraph(path);
        RefreshOrchestrations();
    }

    private AgentNodeViewModel CreateNode(AgentDefinition agent)
    {
        var node = new AgentNodeViewModel(agent);
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentNodeViewModel.Name))
                foreach (var c in Connections.Where(c => c.Source.Node == node || c.Target.Node == node))
                    c.RefreshHeader();
        };
        return node;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddAgent()
    {
        var provider = _services.Settings.Providers.FirstOrDefault();
        var offset = Nodes.Count * 30;
        var agent = new AgentDefinition
        {
            Name = $"Agent {Nodes.Count + 1}",
            ProviderId = SelectedNode?.ProviderId ?? provider?.Id ?? "",
            ModelId = SelectedNode?.ModelId ?? "",
            X = (SelectedNode?.Location.X + 320) ?? 100 + offset,
            Y = (SelectedNode?.Location.Y) ?? 150 + offset,
        };
        Graph.Agents.Add(agent);
        var node = CreateNode(agent);
        Nodes.Add(node);
        SelectedNode = node;
    }

    public void Connect(ConnectorViewModel a, ConnectorViewModel b)
    {
        if (IsRunning || a.IsInput == b.IsInput) return;
        var source = a.IsInput ? b : a;
        var target = a.IsInput ? a : b;
        if (source.Node == target.Node) return;
        if (Connections.Any(c => c.Source == source && c.Target == target)) return;

        var arrow = new ArrowDefinition
        {
            SourceAgentId = source.Node.Id,
            TargetAgentId = target.Node.Id,
            Name = $"to_{MakeIdentifier(target.Node.Name)}",
        };
        Graph.Arrows.Add(arrow);
        var vm = new ArrowViewModel(arrow, source, target);
        Connections.Add(vm);
        SelectedConnection = vm;
    }

    [RelayCommand]
    private void DisconnectConnector(ConnectorViewModel? connector)
    {
        if (connector is null || IsRunning) return;
        foreach (var c in Connections.Where(c => c.Source == connector || c.Target == connector).ToList())
            RemoveConnection(c);
    }

    [RelayCommand]
    private void RemoveConnection(ArrowViewModel? connection)
    {
        if (connection is null || IsRunning) return;
        Connections.Remove(connection);
        Graph.Arrows.Remove(connection.Model);
        connection.Source.IsConnected = Connections.Any(c => c.Source == connection.Source);
        connection.Target.IsConnected = Connections.Any(c => c.Target == connection.Target);
        if (SelectedConnection == connection) SelectedConnection = null;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DeleteSelected()
    {
        if (SelectedConnection is { } arrow)
        {
            RemoveConnection(arrow);
            return;
        }
        if (SelectedNode is not { } node) return;
        DisconnectConnector(node.Input);
        DisconnectConnector(node.Output);
        Nodes.Remove(node);
        Graph.Agents.Remove(node.Model);
        SelectedNode = null;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DuplicateSelected()
    {
        if (SelectedNode is not { } node) return;
        var copy = JsonSerializer.Deserialize<AgentDefinition>(JsonSerializer.Serialize(node.Model, Json.Options), Json.Options)!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name += " (copy)";
        copy.X += 40;
        copy.Y += 40;
        Graph.Agents.Add(copy);
        var vm = CreateNode(copy);
        Nodes.Add(vm);
        SelectedNode = vm;
    }

    private bool CanEdit() => !IsRunning;

    // ---------- Dialogs ----------

    [RelayCommand]
    private void ManageSkills() => new SkillsWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private void ManageProviders() => new ProvidersWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private void ShowHistory() => new RunHistoryWindow { Owner = Application.Current.MainWindow }.Show();

    [RelayCommand]
    private void ClearTranscript()
    {
        Transcript.Clear();
        _streaming.Clear();
        _tokenBuffers.Clear();
        _trimNotice = null;
        _trimmedEntries = 0;
        _replayGeneration++;
    }

    /// <summary>Adds an entry to the live transcript, dropping the oldest ones past the cap (full history stays in the run logs).</summary>
    private void AddTranscriptEntry(TranscriptEntryViewModel entry)
    {
        Transcript.Add(entry);
        if (Transcript.Count <= MaxLiveTranscriptEntries) return;

        if (_trimNotice is not null) Transcript.Remove(_trimNotice);
        var drop = Transcript.Count - MaxLiveTranscriptEntries + TranscriptTrimChunk;
        for (var i = 0; i < drop; i++)
            Transcript.RemoveAt(0);
        _trimmedEntries += drop;
        _trimNotice = new TranscriptEntryViewModel { Kind = TranscriptKind.Info, Header = $"… {_trimmedEntries} older entries hidden (see Run history)" };
        Transcript.Insert(0, _trimNotice);
    }

    // ---------- Running ----------

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task RunAsync()
    {
        if (IsComposing)
        {
            await StartNewSessionAsync();
            return;
        }
        if (!ValidateAgents()) return;
        var session = _activeSession;
        if (session is { FolderMissing: true })
        {
            MessageBox.Show($"The session's working directory does not exist:\n{session.WorkingDirectory}\n\nUse Edit session to choose another folder.", "Run", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Without an open session (e.g. after picking an orchestration), the run starts a new session.
        var dialog = new RunDialog(Nodes, SelectedNode ?? Nodes[0], session?.WorkingDirectory ?? _services.Settings.LastWorkingDirectory,
            lockWorkingDirectory: session is not null) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        _services.Settings.LastWorkingDirectory = dialog.WorkingDirectory;
        _services.SaveSettings();
        if (session is null)
        {
            if (FilePath is null && !TrySave()) return;
            session = AddSession(SessionNameFromTask(dialog.TaskText), dialog.WorkingDirectory, FilePath!);
        }
        await ExecuteRunAsync(session, dialog.EntryAgent.Id, dialog.TaskText);
    }

    /// <summary>Shows a message and returns false if the graph has no agents or an agent lacks a provider/model.</summary>
    private bool ValidateAgents()
    {
        if (Nodes.Count == 0)
        {
            MessageBox.Show("Add at least one agent first.", "Run");
            return false;
        }
        var problems = Nodes.Where(n => _services.FindProvider(n.ProviderId) is null || string.IsNullOrWhiteSpace(n.ModelId))
            .Select(n => $"• {n.Name}: choose a provider and model").ToList();
        if (problems.Count == 0) return true;
        MessageBox.Show("Some agents are not configured:\n" + string.Join("\n", problems), "Run", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task ExecuteRunAsync(SessionItemViewModel session, string entryAgentId, string task)
    {
        foreach (var n in Nodes) n.RunState = NodeRunState.Idle;
        foreach (var c in Connections) { c.TraversalCount = 0; c.IsActive = false; }
        _streaming.Clear();
        _tokenBuffers.Clear();

        var snapshot = JsonSerializer.Deserialize<GraphDefinition>(JsonSerializer.Serialize(Graph, Json.Options), Json.Options)!;
        var runner = new GraphRunner(new GraphRunnerOptions
        {
            ChatClientFactory = _services.ChatClients,
            ProviderLookup = _services.FindProvider,
            SkillLookup = _services.Skills.TryGet,
            Approval = _services.Approval,
            UserInput = _services.UserInput,
            Logger = _services.RunLogger,
        });

        _runCts = new CancellationTokenSource();
        _pause = new PauseTokenSource();
        IsRunning = true;
        IsPaused = false;
        StatusText = "Running…";
        _eventPump.Start();
        string? runId = null;
        try
        {
            var result = await Task.Run(() =>
            {
                var request = new RunRequest(entryAgentId, task, session.WorkingDirectory,
                    SessionHistory.Build(_services.RunLogger, session.Model));
                return runner.RunAsync(snapshot, request, e =>
                {
                    runId ??= e.RunId;
                    _pendingEvents.Enqueue(e);
                }, _pause, _runCts.Token);
            });
            PumpEvents();
            StatusText = result.Status switch
            {
                RunStatus.Completed => $"Run completed in {result.Steps} step(s).",
                RunStatus.Cancelled => "Run stopped.",
                _ => $"Run failed: {result.Error}",
            };
        }
        catch (Exception ex)
        {
            PumpEvents();
            StatusText = $"Run failed: {ex.Message}";
        }
        finally
        {
            _eventPump.Stop();
            IsRunning = false;
            IsPaused = false;
            _runCts.Dispose();
            _runCts = null;
            _pause = null;
            foreach (var n in Nodes.Where(n => n.RunState is NodeRunState.Running or NodeRunState.WaitingApproval or NodeRunState.WaitingInput))
                n.RunState = NodeRunState.Idle;
            if (!string.IsNullOrEmpty(runId))
                RecordSessionRun(session, runId);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void PauseResume()
    {
        if (_pause is null) return;
        if (_pause.IsPaused) _pause.Resume(); else _pause.Pause();
        IsPaused = _pause.IsPaused;
        StatusText = IsPaused ? "Paused (finishing current model/tool call)…" : "Running…";
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _pause?.Resume();
        _runCts?.Cancel();
        StatusText = "Stopping…";
    }

    private void NotifyRunCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        PauseResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewGraphCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        AddAgentCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DuplicateSelectedCommand.NotifyCanExecuteChanged();
        NewSessionCommand.NotifyCanExecuteChanged();
        StartNewSessionCommand.NotifyCanExecuteChanged();
        OpenOrchestrationItemCommand.NotifyCanExecuteChanged();
        NewSessionWithOrchestrationCommand.NotifyCanExecuteChanged();
        RemoveOrchestrationCommand.NotifyCanExecuteChanged();
        EditSessionCommand.NotifyCanExecuteChanged();
        RerunSessionCommand.NotifyCanExecuteChanged();
        DeleteSessionCommand.NotifyCanExecuteChanged();
    }

    private void PumpEvents()
    {
        while (_pendingEvents.TryDequeue(out var e))
            Apply(e);
        FlushTokenBuffers();
    }

    /// <summary>Streamed tokens are appended to a buffer and written to the UI once per pump tick.</summary>
    private void FlushTokenBuffers()
    {
        foreach (var (entry, text) in _tokenBuffers)
            entry.Text = text.ToString();
        _tokenBuffers.Clear();
    }

    private void Apply(RunEvent e)
    {
        switch (e.Kind)
        {
            case RunEventKind.Token:
                if (e.ActivationId is null) return;
                if (!_streaming.TryGetValue(e.ActivationId, out var entry))
                {
                    entry = new TranscriptEntryViewModel { Kind = TranscriptKind.Assistant, AgentId = e.AgentId, ActivationId = e.ActivationId, Header = $"{e.AgentName} 💬" };
                    _streaming[e.ActivationId] = entry;
                    AddTranscriptEntry(entry);
                }
                if (!_tokenBuffers.TryGetValue(entry, out var buffer))
                {
                    buffer = new StringBuilder(entry.Text);
                    _tokenBuffers[entry] = buffer;
                }
                buffer.Append(e.Text);
                return;

            case RunEventKind.AssistantMessage:
                if (e.ActivationId is not null && _streaming.Remove(e.ActivationId, out var streamed))
                {
                    _tokenBuffers.Remove(streamed);
                    streamed.Text = e.Text ?? streamed.Text;
                }
                else
                    AddTranscriptEntry(new TranscriptEntryViewModel { Kind = TranscriptKind.Assistant, AgentId = e.AgentId, ActivationId = e.ActivationId, Header = $"{e.AgentName} 💬", Text = e.Text ?? "" });
                return;

            case RunEventKind.NodeStarted:
                SetNodeState(e.AgentId, NodeRunState.Running);
                break;
            case RunEventKind.NodeCompleted:
                SetNodeState(e.AgentId, NodeRunState.Done);
                if (e.ActivationId is not null) _streaming.Remove(e.ActivationId);
                return;
            case RunEventKind.NodeFailed:
                SetNodeState(e.AgentId, NodeRunState.Error);
                break;
            case RunEventKind.ToolCallRequested:
                if (e.ActivationId is not null) _streaming.Remove(e.ActivationId);
                break;
            case RunEventKind.ArrowFired:
                if (Connections.FirstOrDefault(c => c.Model.Id == e.ArrowId) is { } arrow)
                {
                    arrow.TraversalCount++;
                    arrow.IsActive = true;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    timer.Tick += (_, _) => { arrow.IsActive = false; timer.Stop(); };
                    timer.Start();
                }
                break;
        }

        if (TranscriptEntryViewModel.FromEvent(e) is { } item)
            AddTranscriptEntry(item);
    }

    private void SetNodeState(string? agentId, NodeRunState state)
    {
        if (Nodes.FirstOrDefault(n => n.Id == agentId) is { } node)
            node.RunState = state;
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    private static string MakeIdentifier(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars).Trim('_');
    }
}

/// <summary>Inspector content shown when nothing is selected.</summary>
public sealed class GraphSettingsViewModel(MainViewModel main)
{
    public MainViewModel Main { get; } = main;
}
