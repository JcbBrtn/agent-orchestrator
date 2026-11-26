using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace AgentOrchestrator.App.ViewModels;

/// <summary>Orchestrations: the graph files (in the graphs folder or opened from elsewhere) the user can open or start a session with.</summary>
public partial class MainViewModel
{
    public ObservableCollection<OrchestrationItemViewModel> Orchestrations { get; } = [];

    /// <summary>The orchestration open in the editor, highlighted only when no session is active. Setting it opens that graph.</summary>
    public OrchestrationItemViewModel? SelectedOrchestration
    {
        get => _activeSession is null && !IsComposing ? Orchestrations.FirstOrDefault(o => o.IsOpen) : null;
        set
        {
            if (value is null || value.IsOpen)
            {
                if (value is not null && !IsRunning)
                {
                    IsComposing = false;
                    LeaveSession();
                }
                Dispatch(() => OnPropertyChanged(nameof(SelectedOrchestration)));
                return;
            }
            OpenOrchestration(value);
            Dispatch(() => OnPropertyChanged(nameof(SelectedOrchestration)));
        }
    }

    private void RefreshOrchestrations()
    {
        var graphsDir = _services.Paths.GraphsDir;
        var paths = _services.Graphs.List()
            .Concat(_services.Settings.KnownGraphPaths)
            .Concat(FilePath is null ? [] : [FilePath])
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var items = new List<OrchestrationItemViewModel>();
        foreach (var path in paths)
        {
            string name;
            int agents;
            string description;
            IReadOnlyList<AgentOrchestrator.Core.Models.GraphReference> references;
            try
            {
                var graph = _services.Graphs.Load(path);
                name = string.IsNullOrWhiteSpace(graph.Name) ? Path.GetFileNameWithoutExtension(path) : graph.Name;
                agents = graph.Agents.Count;
                description = graph.Description;
                references = graph.References;
            }
            catch
            {
                continue;
            }
            var inGraphsDir = string.Equals(Path.GetDirectoryName(path), graphsDir, StringComparison.OrdinalIgnoreCase);
            items.Add(new OrchestrationItemViewModel(path, name, agents, inGraphsDir, description, references)
            {
                IsOpen = string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase),
            });
        }

        Orchestrations.Clear();
        foreach (var item in items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            Orchestrations.Add(item);
        OnPropertyChanged(nameof(SelectedOrchestration));
    }

    /// <summary>Remembers graph files outside the graphs folder so they stay listed.</summary>
    private void RememberGraph(string path)
    {
        if (string.Equals(Path.GetDirectoryName(path), _services.Paths.GraphsDir, StringComparison.OrdinalIgnoreCase)) return;
        if (_services.Settings.KnownGraphPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _services.Settings.KnownGraphPaths.Add(path);
        _services.SaveSettings();
    }

    private bool OpenOrchestration(OrchestrationItemViewModel item)
    {
        if (IsRunning || !ConfirmDiscard()) return false;
        try
        {
            LoadGraph(_services.Graphs.Load(item.Path), item.Path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open orchestration:\n{ex.Message}", "Orchestrations", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshOrchestrations();
            return false;
        }
        _services.Settings.LastGraphPath = item.Path;
        _services.SaveSettings();
        IsComposing = false;
        LeaveSession();
        return true;
    }

    /// <summary>Switches from the active session to plain orchestration editing; the next Run starts a new session.</summary>
    private void LeaveSession()
    {
        if (_activeSession is null) return;
        SetActiveSession(null);
        ClearTranscript();
        foreach (var n in Nodes) n.RunState = NodeRunState.Idle;
        StatusText = "Editing orchestration — Run (F5) starts a new session.";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void OpenOrchestrationItem(OrchestrationItemViewModel? item)
    {
        if (item is not null) OpenOrchestration(item);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewSessionWithOrchestration(OrchestrationItemViewModel? item)
    {
        if (item is null) return;
        ShowComposer(item.Path);
    }

    [RelayCommand]
    private void ShowOrchestrationReferences(OrchestrationItemViewModel? item)
    {
        if (item is null || !item.HasReferences) return;
        var text = item.Description + "\n\nBased on:\n" + string.Join("\n", item.References.Select(r => $"• {r.Title} — {r.Authors}\n  {r.Url}"))
            + "\n\nOpen these links in your browser?";
        if (MessageBox.Show(text, item.Name + " — references", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        foreach (var r in item.References.Where(r => Uri.IsWellFormedUriString(r.Url, UriKind.Absolute)))
            Process.Start(new ProcessStartInfo(r.Url) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ShowOrchestrationInFolder(OrchestrationItemViewModel? item)
    {
        if (item is null || !File.Exists(item.Path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveOrchestration(OrchestrationItemViewModel? item)
    {
        if (item is null) return;
        if (item.InGraphsDir)
        {
            if (MessageBox.Show($"Delete orchestration '{item.Name}'?\n\n{item.Path}\n\nSessions that use it fall back to the graph saved with their latest run.",
                    "Delete orchestration", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try { File.Delete(item.Path); }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not delete:\n{ex.Message}", "Delete orchestration", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (item.IsOpen)
            {
                FilePath = null; // keep it in the editor as an unsaved graph
                _savedJson = "";
            }
        }
        else
        {
            _services.Settings.KnownGraphPaths.RemoveAll(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));
            _services.SaveSettings();
            if (item.IsOpen)
            {
                StatusText = "Removed from the list; it stays open until you open another orchestration.";
                Orchestrations.Remove(item);
                OnPropertyChanged(nameof(SelectedOrchestration));
                return;
            }
        }
        RefreshOrchestrations();
    }
}
