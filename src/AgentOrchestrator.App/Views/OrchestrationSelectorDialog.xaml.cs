using System.Windows;
using System.Windows.Controls;
using AgentOrchestrator.App.Services;
using AgentOrchestrator.App.ViewModels;
using AgentOrchestrator.Core.Models;

namespace AgentOrchestrator.App.Views;

/// <summary>Picks an orchestration to add to the current graph, and which of its entry points becomes the primary hook.</summary>
public partial class OrchestrationSelectorDialog : Window
{
    private sealed record HookChoice(string AgentId, string Name);

    private GraphDefinition? _loaded;

    public OrchestrationSelectorDialog(IEnumerable<OrchestrationItemViewModel> orchestrations)
    {
        InitializeComponent();
        OrchestrationListBox.ItemsSource = orchestrations.ToList();
        if (OrchestrationListBox.Items.Count > 0)
            OrchestrationListBox.SelectedIndex = 0;
    }

    public OrchestrationItemViewModel? SelectedOrchestration => OrchestrationListBox.SelectedItem as OrchestrationItemViewModel;
    public GraphDefinition? SelectedGraph { get; private set; }
    public string? PrimaryHookAgentId { get; private set; }

    private void OnOrchestrationChanged(object sender, SelectionChangedEventArgs e)
    {
        _loaded = null;
        HookComboBox.ItemsSource = null;
        if (SelectedOrchestration is null)
            return;

        try
        {
            _loaded = AppServices.Instance.Graphs.Load(SelectedOrchestration.Path);
            var hooks = GraphMerger.FindEntryPointIds(_loaded)
                .Select(id => new HookChoice(id, _loaded.Agents.First(a => a.Id == id).Name))
                .ToList();
            HookComboBox.ItemsSource = hooks;
            HookComboBox.SelectedIndex = hooks.Count > 0 ? 0 : -1;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not read the orchestration:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSelect(object sender, RoutedEventArgs e)
    {
        if (_loaded is null)
            return;
        if (HookComboBox.SelectedItem is not HookChoice hook)
        {
            MessageBox.Show(this, "This orchestration has no entry point to start from.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedGraph = _loaded;
        PrimaryHookAgentId = hook.AgentId;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
