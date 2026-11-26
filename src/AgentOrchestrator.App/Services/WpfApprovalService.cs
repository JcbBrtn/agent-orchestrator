using System.Windows;
using AgentOrchestrator.App.Views;
using AgentOrchestrator.Core.Tools;

namespace AgentOrchestrator.App.Services;

/// <summary>Shows approval prompts on the UI thread, one at a time (parallel branches queue up).</summary>
public sealed class WpfApprovalService : IApprovalService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler<ApprovalRequest>? WaitingForApproval;
    public event EventHandler<ApprovalRequest>? ApprovalFinished;

    public async Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        WaitingForApproval?.Invoke(this, request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new ApprovalDialog(request) { Owner = Application.Current.MainWindow };
                dialog.ShowDialog();
                return dialog.Decision;
            }).Task;
        }
        finally
        {
            _gate.Release();
            ApprovalFinished?.Invoke(this, request);
        }
    }
}
