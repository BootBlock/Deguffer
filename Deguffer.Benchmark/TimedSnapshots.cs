using Deguffer.Core.Exploring;

namespace Deguffer.Benchmark;

/// <summary>
/// Snapshots assembled as an Explore scan assembles them, on the thread pool and ordered by name, and
/// timed as each is assembled.
///
/// <para>Each assembling is timed where it runs, so the time is the snapshot's own pass, the copy of
/// what it is drawn from included, and not how long it waited for a thread.</para>
/// </summary>
internal sealed class TimedSnapshots
{
    private readonly TimeProvider _clock;
    private int _count;
    private long _ticks;

    /// <param name="clock">What each assembling is timed by.</param>
    public TimedSnapshots(TimeProvider clock)
    {
        _clock = clock;
        Assembly = SnapshotAssembly.ByName with
        {
            Start = assemble => SnapshotAssembly.ByName.Start(() => Timed(assemble)),
        };
    }

    /// <summary>How the read or walk assembles its snapshots.</summary>
    public SnapshotAssembly Assembly { get; }

    /// <summary>
    /// The snapshots assembled so far. Read once the read or walk has returned, which waits for the
    /// last assembling to stop (<see cref="ScanSnapshots.Stop"/>).
    /// </summary>
    public SnapshotTimes Times =>
        new(Volatile.Read(ref _count), TimeSpan.FromSeconds(Interlocked.Read(ref _ticks) / (double)_clock.TimestampFrequency));

    private void Timed(Action assemble)
    {
        var started = _clock.GetTimestamp();

        try
        {
            assemble();
        }
        finally
        {
            // One snapshot is assembled at a time, but on whichever pool thread, and read from
            // another, so the counts are published as they are written.
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _ticks, _clock.GetTimestamp() - started);
        }
    }
}
