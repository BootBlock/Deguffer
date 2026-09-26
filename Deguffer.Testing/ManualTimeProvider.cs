namespace Deguffer.Testing;

/// <summary>
/// A clock that moves only when a test says so, for code that waits through a
/// <see cref="TimeProvider"/>.
///
/// <para>Only one-shot timers, which is what <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// creates. A periodic one is refused rather than fired once and forgotten, which would read as
/// a timer that stopped.</para>
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>How many timers are armed and not yet due.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => timer.Due is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
        {
            throw new NotSupportedException("This clock only fires one-shot timers.");
        }

        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Completes once something is waiting on this clock.</summary>
    public async Task WhenWaitingAsync(TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;

        while (Waiting == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Nothing began waiting on the clock.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Move the clock on, and fire every timer that has come due.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;

        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(timer => timer.Due <= _now)];

            foreach (var timer in due)
            {
                timer.Due = null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (!clock._timers.Contains(this))
                {
                    clock._timers.Add(this);
                }

                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
