using TheCardPlace.Core;

namespace TheCardPlace.Play;

/// <summary>
/// The pause between computer turns. Timed paces are a ceiling rather than a wait the player is
/// held to: Continue is on screen throughout, and pressing it ends the pause early. Wait for me
/// has no timer at all and only Continue moves the game on.
/// </summary>
public sealed class PaceGate
{
    private TaskCompletionSource? _pending;
    private CancellationTokenSource? _timer;

    /// <summary>True while the game is paused for the pace or for Continue.</summary>
    public bool IsWaiting => _pending is not null;

    /// <summary>Raised when <see cref="IsWaiting"/> changes, so Continue can appear and go.</summary>
    public event Action? Changed;

    public Task WaitAsync(Pace pace, CancellationToken token)
    {
        if (pace == Pace.Immediate || token.IsCancellationRequested) return Task.CompletedTask;
        // Never two at once; a second caller ends the first wait.
        Fire();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        var timer = new CancellationTokenSource();
        _timer = timer;
        // The timer fires on a pool thread; the gate's state, and whoever listens to Changed,
        // belong to the thread that started the wait (the window's, in the app).
        var home = SynchronizationContext.Current;
        void FireAtHome()
        {
            if (home is null) FireIf(tcs);
            else home.Post(_ => FireIf(tcs), null);
        }
        token.Register(FireAtHome);
        if (pace.Delay() is { } delay)
        {
            _ = Task.Delay(delay, timer.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) FireAtHome();
            }, TaskScheduler.Default);
        }
        Changed?.Invoke();
        return tcs.Task;
    }

    /// <summary>The Continue button.</summary>
    public void Continue() => Fire();

    public void Cancel() => Fire();

    private void FireIf(TaskCompletionSource tcs)
    {
        if (ReferenceEquals(_pending, tcs)) Fire();
        else tcs.TrySetResult();
    }

    private void Fire()
    {
        var p = _pending;
        if (p is null) return;
        _pending = null;
        _timer?.Cancel();
        _timer = null;
        Changed?.Invoke();
        p.TrySetResult();
    }
}
