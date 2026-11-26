using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AgentOrchestrator.App.Services;
using AgentOrchestrator.Core.Engine;

namespace AgentOrchestrator.App.Views;

public partial class RunHistoryWindow : Window
{
    private readonly RunLogger _logger = AppServices.Instance.RunLogger;

    public RunHistoryWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        Refresh();
    }

    private void Refresh() => RunGrid.ItemsSource = _logger.ListRuns();

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnRunSelected(object sender, SelectionChangedEventArgs e) =>
        EventGrid.ItemsSource = RunGrid.SelectedItem is RunSummary run ? _logger.LoadEvents(run.RunId) : null;

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = RunGrid.SelectedItem is RunSummary run ? Path.Combine(_logger.RunsDirectory, run.RunId) : _logger.RunsDirectory;
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
