using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using AgentOrchestrator.App.ViewModels;

namespace AgentOrchestrator.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = (MainViewModel)DataContext;
        ((INotifyCollectionChanged)TranscriptList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && TranscriptList.Items.Count > 0)
                TranscriptList.ScrollIntoView(TranscriptList.Items[^1]);
        };
    }

    private void OnToolBarLoaded(object sender, RoutedEventArgs e) => Services.ThemeManager.HideToolBarOverflow((System.Windows.Controls.ToolBar)sender);

    private void OnNewSessionPageVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => { PromptBox.Focus(); PromptBox.CaretIndex = PromptBox.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        if (vm.IsRunning)
        {
            if (MessageBox.Show("A run is in progress. Stop it and exit?", "Agent Orchestrator", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            vm.StopCommand.Execute(null);
        }
        if (!vm.ConfirmDiscard())
            e.Cancel = true;
        base.OnClosing(e);
    }
}
