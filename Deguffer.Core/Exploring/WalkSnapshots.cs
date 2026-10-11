namespace Deguffer.Core.Exploring;

/// <summary>
/// When a walk still in progress hands over a tree to draw, and the assembling of each one away
/// from the walk's workers.
///
/// <para>A snapshot is a pass over every node found so far, so its cost grows with the walk. Taken
/// on a worker, under the lock every worker needs, it stopped the whole walk; started on a fixed
/// cadence, a snapshot that took longer than the interval was followed by the next at once, and a
/// large walk did little else. Here only a capture is taken on the walk, under the lock (see
/// <see cref="ExploreTreeBuilder.Capture"/>), one snapshot is assembled at a time somewhere else,
/// and the next is not started until a multiple of the last one's time has passed since it
/// ended.</para>
///
/// <para>Driven by the walk's progress reports, which are never made beside each other, so nothing
/// here is called from two threads at once except the assembling.</para>
/// </summary>
internal sealed class WalkSnapshots
{
    /// <summary>
    /// The least time from the end of one snapshot to the start of the next.
    ///
    /// <para>The reason to take one at all is that a scan of a full drive is long enough that an
    /// unchanging window reads as a hung one. Three quarters of a second is fast enough to look
    /// alive.</para>
    ///
    /// <para>Every disk tool surveyed for this feature — WinDirStat, KDirStat, QDirStat, Filelight,
    /// Baobab, GrandPerspective, Disk Inventory X — refuses to draw its map until the scan finishes,
    /// and DaisyDisk tried live rings and abandoned them. The measured reason is layout instability:
    /// on the Bederson/Shneiderman/Wattenberg change metric a squarified treemap scores 14.82
    /// against slice-and-dice's 0.25, so a map redrawn from growing data rearranges itself
    /// continuously. Deguffer draws its snapshots anyway, and answers that instead — a snapshot is
    /// ordered by <see cref="ExploreChildOrder.ByName"/>, under which a growing child widens where
    /// it already is rather than overtaking its siblings.</para>
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// How many times its own assembling time a snapshot waits, from its end, before the next
    /// starts, where that is longer than <see cref="Interval"/>. Four keeps snapshots to a fifth of
    /// one processor however large the tree grows, so the walk keeps the rest.
    /// </summary>
    public const int AssemblingTimes = 4;

    /// <summary>How a scan assembles a snapshot: on the thread pool, apart from the walk's workers.</summary>
    public static Func<Action, Task> InBackground { get; } = static assemble => Task.Run(assemble);

    private readonly ExploreTreeBuilder _builder;
    private readonly TimeProvider _clock;
    private readonly Func<Action, Task> _start;
    private readonly long _intervalTicks;

    private long _due;
    private long _started;
    private Task? _assembling;
    private ExploreTree? _ready;
    private volatile bool _stopped;

    /// <param name="start">
    /// Runs one assembling and says when it has finished. <see cref="InBackground"/> outside tests.
    /// </param>
    public WalkSnapshots(ExploreTreeBuilder builder, TimeProvider clock, Func<Action, Task> start)
    {
        _builder = builder;
        _clock = clock;
        _start = start;
        _intervalTicks = (long)(Interval.TotalSeconds * clock.TimestampFrequency);
        _due = clock.GetTimestamp() + _intervalTicks;
    }

    /// <summary>
    /// The snapshot finished since the last call, if there is one, and the next one started if it
    /// is due and none is being assembled. Called from the walk's progress report.
    ///
    /// <para>A finished snapshot waits for the next report rather than making one of its own, so
    /// every report still comes from the walk, one at a time and with its counts in order, and none
    /// can come after the walk has returned.</para>
    /// </summary>
    public ExploreTree? Take()
    {
        var ready = Interlocked.Exchange(ref _ready, null);

        if (_assembling is { } assembling)
        {
            if (!assembling.IsCompleted)
            {
                return ready;
            }

            // A snapshot that failed is a defect, and it fails the walk rather than leaving every
            // snapshot after it silently missing.
            assembling.GetAwaiter().GetResult();
            _assembling = null;
        }

        var now = _clock.GetTimestamp();
        if (now < _due)
        {
            return ready;
        }

        var capture = _builder.Capture();
        _started = now;
        _assembling = _start(() => Assemble(capture));

        return ready;
    }

    /// <summary>
    /// Start no more, and return once the snapshot being assembled, if any, has stopped. One that
    /// has not begun its passes is abandoned: the walk is over, and the tree that replaces it is
    /// being built.
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        _assembling?.GetAwaiter().GetResult();
    }

    private void Assemble(ExploreTreeCapture capture)
    {
        if (_stopped)
        {
            return;
        }

        var tree = capture.Build(ExploreChildOrder.ByName);
        var ended = _clock.GetTimestamp();

        // Read by the walk only once it has seen this finish, so the task's completion publishes it.
        _due = ended + Math.Max(_intervalTicks, (ended - _started) * AssemblingTimes);
        Volatile.Write(ref _ready, tree);
    }
}
