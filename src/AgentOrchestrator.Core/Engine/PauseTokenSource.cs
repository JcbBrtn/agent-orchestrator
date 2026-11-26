namespace AgentOrchestrator.Core.Engine;

/// <summary>Cooperative pause: the engine awaits <see cref="WaitWhilePausedAsync"/> before each model call and tool call.</summary>
public sealed class PauseTokenSource
{
    private readonly object _gate = new();
    private TaskCompletionSource? _paused;

    public bool IsPaused
    {
        get { lock (_gate) return _paused is not null; }
    }

    public event EventHandler<bool>? PausedChanged;

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused is not null) return;
            _paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        PausedChanged?.Invoke(this, true);
    }

    public void Resume()
    {
        TaskCompletionSource? tcs;
        lock (_gate)
        {
            tcs = _paused;
            _paused = null;
        }
        if (tcs is null) return;
        tcs.TrySetResult();
        PausedChanged?.Invoke(this, false);
    }

    public Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (_gate) wait = _paused?.Task;
        return wait is null ? Task.CompletedTask : wait.WaitAsync(cancellationToken);
    }
}
