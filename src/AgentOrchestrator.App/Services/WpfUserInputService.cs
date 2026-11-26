using System.Windows;
using AgentOrchestrator.App.Views;
using AgentOrchestrator.Core.Tools;

namespace AgentOrchestrator.App.Services;

/// <summary>Shows agent questions on the UI thread, one at a time (parallel branches queue up).</summary>
public sealed class WpfUserInputService : IUserInputService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler<UserQuestion>? WaitingForUser;
    public event EventHandler<UserQuestion>? UserAnswered;

    public async Task<string?> AskAsync(UserQuestion question, CancellationToken cancellationToken)
    {
        WaitingForUser?.Invoke(this, question);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var owner = Application.Current.MainWindow;
                var dialog = new AskUserDialog(question) { Owner = owner };
                owner?.Activate();
                dialog.ShowDialog();
                return dialog.Answer;
            }).Task;
        }
        finally
        {
            _gate.Release();
            UserAnswered?.Invoke(this, question);
        }
    }
}
