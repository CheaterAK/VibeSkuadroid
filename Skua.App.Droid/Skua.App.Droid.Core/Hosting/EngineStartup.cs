namespace Skua.App.Droid.Core.Hosting;

public enum EngineStartupState
{
    /// <summary>No attempt yet.</summary>
    NotStarted,

    /// <summary>An attempt is running on a background thread.</summary>
    Starting,

    /// <summary>Composed; <see cref="EngineStartup{T}.Value"/> is set for the rest of the process.</summary>
    Ready,

    /// <summary>The last attempt threw; the next <see cref="EngineStartup{T}.EnsureStarted"/> tries again.</summary>
    Failed,
}

/// <summary>
/// Composes a process-wide object (the engine) once, on a background thread, so no caller ever blocks on it: the
/// Android main thread (Application.OnCreate, activities, the service, receivers) only starts it and continues when it
/// is done. Concurrent callers share one attempt. A failing attempt never throws to callers: its exception goes to the
/// observer first (the crash report), then every waiter gets null, and a later <see cref="EnsureStarted"/> retries.
/// </summary>
public sealed class EngineStartup<T> where T : class
{
    private readonly object _gate = new();
    private readonly Func<T> _compose;
    private readonly Action<EngineStartupState, Exception?>? _observer;
    private readonly Action<Action> _run;
    private Task<T?>? _attempt;
    private volatile T? _value;
    private EngineStartupState _state;
    private Exception? _error;

    /// <param name="compose">Builds the object; runs on a background thread, may take seconds, may throw.</param>
    /// <param name="observer">
    /// Told when an attempt starts (<see cref="EngineStartupState.Starting"/>, on the caller's thread) and how it ended
    /// (<see cref="EngineStartupState.Ready"/> or <see cref="EngineStartupState.Failed"/> with the exception, on the
    /// background thread, before any waiter continues). Its own exceptions are ignored.
    /// </param>
    /// <param name="run">Runs an attempt off the calling thread. Default: a dedicated (long-running) thread.</param>
    public EngineStartup(Func<T> compose, Action<EngineStartupState, Exception?>? observer = null, Action<Action>? run = null)
    {
        _compose = compose;
        _observer = observer;
        _run = run ?? (work => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
    }

    public EngineStartupState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>The composed object, or null while it is not (yet) available. Safe from any thread.</summary>
    public T? Value => _value;

    /// <summary>Why the last attempt failed (null unless <see cref="State"/> is <see cref="EngineStartupState.Failed"/>).</summary>
    public Exception? Error
    {
        get
        {
            lock (_gate)
                return _error;
        }
    }

    /// <summary>
    /// Starts an attempt unless one is running or the object is ready, and returns immediately. The task completes
    /// with the object, or with null when the attempt failed; it never faults and never completes on the calling
    /// thread's stack.
    /// </summary>
    public Task<T?> EnsureStarted()
    {
        TaskCompletionSource<T?> attempt;
        lock (_gate)
        {
            if (_state == EngineStartupState.Ready)
                return Task.FromResult(_value);
            if (_state == EngineStartupState.Starting)
                return _attempt!;
            attempt = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _attempt = attempt.Task;
            _state = EngineStartupState.Starting;
            _error = null;
        }
        Notify(EngineStartupState.Starting, null);
        try
        {
            _run(() => Compose(attempt));
        }
        catch (Exception e)
        {
            // The runner itself failed (no thread): the attempt fails like a composition failure.
            Finish(attempt, null, e);
        }
        return attempt.Task;
    }

    /// <summary>Waits for the running attempt (starting one if needed) at most <paramref name="timeout"/>; null when it failed or timed out.</summary>
    public async Task<T?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task<T?> attempt = EnsureStarted();
        try
        {
            return await attempt.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private void Compose(TaskCompletionSource<T?> attempt)
    {
        T? value = null;
        Exception? error = null;
        try
        {
            value = _compose() ?? throw new InvalidOperationException("Engine composition returned nothing.");
        }
        catch (Exception e)
        {
            error = e;
        }
        Finish(attempt, value, error);
    }

    private void Finish(TaskCompletionSource<T?> attempt, T? value, Exception? error)
    {
        lock (_gate)
        {
            if (error is null)
            {
                _value = value;
                _state = EngineStartupState.Ready;
            }
            else
            {
                _state = EngineStartupState.Failed;
                _error = error;
            }
        }
        // Recorded before anyone waiting continues: the degraded UI can rely on the report being there.
        Notify(error is null ? EngineStartupState.Ready : EngineStartupState.Failed, error);
        attempt.TrySetResult(value);
    }

    private void Notify(EngineStartupState state, Exception? error)
    {
        try
        {
            _observer?.Invoke(state, error);
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }
}
