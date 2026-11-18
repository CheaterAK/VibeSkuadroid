namespace Skua.Core.Headless.Tests.Fakes;

/// <summary>A clock that only moves when told to; its timers fire during <see cref="Advance"/>.</summary>
public sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset end;
        lock (_gate)
            end = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= end).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = end;
                    return;
                }
                _now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero ? _now + next.Period : null;
            }
            next.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer t = new(this, callback, state);
        t.Change(dueTime, period);
        lock (_gate)
            _timers.Add(t);
        return t;
    }

    public int ActiveTimers
    {
        get { lock (_gate) return _timers.Count(t => t.Due is not null); }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTime _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTime owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            }
            return true;
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                Due = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
