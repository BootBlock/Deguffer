namespace Deguffer.Testing;

/// <summary>
/// A clock that moves only when a test says so, for code that waits through a
/// <see cref="TimeProvider"/>.
///
/// <para>Only one-shot timers, which is what <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// creates. A periodic one is refused rather than fired once and forgotten, which would read as
/// a timer that stopped.</para>
///
/// <para>The timestamp moves with the clock too. The base class reads it from the machine's
/// counter, so code that measures elapsed time with <see cref="TimeProvider.GetElapsedTime(long)"/>
/// would otherwise see real time pass under a clock the test is holding still.</para>
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Completed, and replaced, each time a timer is armed. Read and swapped under <see cref="_gate"/>.</summary>
    private TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

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

    /// <summary>
    /// Completes once something is waiting on this clock.
    ///
    /// <para>Told by the timer as it is armed rather than found by polling, so a test can step a
    /// watch through thousands of its own waits: a poll sleeps at least one tick of the machine's
    /// timer each time it finds nothing armed.</para>
    /// </summary>
    public async Task WhenWaitingAsync(TimeSpan patience)
    {
        Task armed;

        lock (_gate)
        {
            if (_timers.Any(timer => timer.Due is not null))
            {
                return;
            }

            armed = _armed.Task;
        }

        try
        {
            await armed.WaitAsync(patience);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Nothing began waiting on the clock.");
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

                if (Due is not null)
                {
                    clock._armed.SetResult();
                    clock._armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
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
