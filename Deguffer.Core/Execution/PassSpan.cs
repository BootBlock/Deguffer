namespace Deguffer.Core.Execution;

/// <summary>
/// One pass of a removal that runs beside its other passes, timed from the moment its first piece of
/// work began to the moment its last ended, and counted, from every thread that works on it at once.
/// See <see cref="RemovalPasses"/>.
/// </summary>
/// <param name="time">The clock the pass is timed on.</param>
internal sealed class PassSpan(TimeProvider time)
{
    private long _first = long.MaxValue;
    private long _last = long.MinValue;
    private long _entries;

    /// <summary>A piece of the pass's work is beginning. Hand what this returns to <see cref="Ended"/>.</summary>
    public long Beginning()
    {
        var now = time.GetTimestamp();
        Lower(ref _first, now);
        return now;
    }

    /// <summary>The piece of work begun at <paramref name="began"/> has ended.</summary>
    public void Ended(long began)
    {
        Raise(ref _last, Math.Max(began, time.GetTimestamp()));
        Interlocked.Increment(ref _entries);
    }

    /// <summary>What the pass took, read once every thread working on it has ended. Zero where it never began.</summary>
    public PassTiming Timing
    {
        get
        {
            var entries = Interlocked.Read(ref _entries);
            return entries == 0
                ? default
                : new PassTiming(time.GetElapsedTime(Interlocked.Read(ref _first), Interlocked.Read(ref _last)), entries);
        }
    }

    private static void Lower(ref long field, long value)
    {
        for (var seen = Interlocked.Read(ref field); value < seen; )
        {
            var was = Interlocked.CompareExchange(ref field, value, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    private static void Raise(ref long field, long value)
    {
        for (var seen = Interlocked.Read(ref field); value > seen; )
        {
            var was = Interlocked.CompareExchange(ref field, value, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }
}
