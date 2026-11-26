using System.Windows;
using System.Windows.Threading;

namespace AgentOrchestrator.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Services.ThemeManager.Apply(Services.AppServices.Instance.Settings.Theme);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop the Copilot runtime child process, if it was started.
        Task.Run(() => Services.AppServices.Instance.Copilot.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.GetBaseException().Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
