namespace Deguffer.Core.Exploring;

/// <summary>
/// When a scan still in progress hands over a tree to draw, and the assembling of each one away
/// from the scan's own threads.
///
/// <para>A snapshot is a pass over every node found so far, or over every record slot of the file
/// table, so it is costly however early it is taken. Taken
/// on a walk worker, under the lock every worker needs, it stopped the whole walk; started on a
/// fixed cadence, a snapshot that took longer than the interval was followed by the next at once,
/// and a large walk did little else. Here the report only takes what a snapshot is assembled from,
/// one snapshot is assembled at a time somewhere else, and the next is not started until a multiple
/// of the last one's time has passed since it ended.</para>
///
/// <para>One cadence for both routes. What each route hands over differs: the walk captures its
/// builder under the lock (<see cref="ExploreTreeBuilder.Capture"/>), and the file table's read
/// copies its columns while the parse threads go on writing them
/// (<see cref="MftExploreColumns.CopyPresent"/>). Either way a snapshot is ordered as
/// <see cref="SnapshotAssembly.Order"/> says when it is captured, which is how a change of view while
/// the scan runs reaches the next snapshot.</para>
///
/// <para>Driven by the scan's progress reports, which are never made beside each other, so nothing
/// here is called from two threads at once except the assembling.</para>
/// </summary>
internal sealed class ScanSnapshots
{
    /// <summary>
    /// The least time from the end of one snapshot to the start of the next. It spaces snapshots,
    /// and is not a wait before the first: that one is due at the scan's first report.
    ///
    /// <para>The reason to take one at all is that a scan of a full drive is long enough that an
    /// unchanging window reads as a hung one. Three quarters of a second apart is often enough to
    /// look alive. Waiting that long for the first only left the page without a map. A walk's first
    /// holds what its first folder held, so it costs next to nothing to assemble, and
    /// <see cref="AssemblingTimes"/> spaces every later snapshot, on either route, by what it cost.</para>
    ///
    /// <para>Every disk tool surveyed for this feature — WinDirStat, KDirStat, QDirStat, Filelight,
    /// Baobab, GrandPerspective, Disk Inventory X — refuses to draw its map until the scan finishes,
    /// and DaisyDisk tried live rings and abandoned them. The measured reason is layout instability:
    /// on the Bederson/Shneiderman/Wattenberg change metric a squarified treemap scores 14.82
    /// against slice-and-dice's 0.25, so a map redrawn from growing data rearranges itself
    /// continuously. Deguffer draws its snapshots anyway. Where the reader's view allows it, a snapshot
    /// is ordered by <see cref="ExploreChildOrder.ByName"/>, under which a growing child widens where
    /// it already is rather than overtaking its siblings. The treemap and the sunburst cannot be drawn
    /// from that order, and a reader who chose one is shown it rearranging rather than another
    /// picture (<see cref="Rendering.ExploreSurface.SnapshotOrder"/>).</para>
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// How many times its own assembling time a snapshot waits, from its end, before the next
    /// starts, where that is longer than <see cref="Interval"/>. Four keeps snapshots to a fifth of
    /// one processor however large the tree grows, so the scan keeps the rest.
    /// </summary>
    public const int AssemblingTimes = 4;

    /// <summary>How a scan assembles a snapshot: on the thread pool, apart from the scan's own threads.</summary>
    public static Func<Action, Task> InBackground { get; } = static assemble => Task.Run(assemble);

    private readonly Func<ExploreChildOrder, Func<CancellationToken, ExploreTree?>> _capture;
    private readonly TimeProvider _clock;
    private readonly SnapshotAssembly _assembly;
    private readonly long _intervalTicks;

    private long _due;
    private long _started;
    private Task? _assembling;
    private readonly CancellationTokenSource _stopping = new();

    // Taken once, so an assembling that outlives Stop still asks a token rather than a disposed
    // source.
    private readonly CancellationToken _stopped;

    private ExploreTree? _ready;

    /// <param name="capture">
    /// Called on the reporting thread when a snapshot is due, with the order its children are to be
    /// in. Takes whatever has to be taken there, and returns the assembling, which runs elsewhere and
    /// gives the tree in that order, marked as a snapshot, or null where there is nothing to draw yet.
    /// The assembling stops for the token it is given.
    /// </param>
    /// <param name="assembly">Where each assembling runs, and the order each snapshot is asked for in.</param>
    public ScanSnapshots(
        Func<ExploreChildOrder, Func<CancellationToken, ExploreTree?>> capture,
        TimeProvider clock,
        SnapshotAssembly assembly)
    {
        _capture = capture;
        _clock = clock;
        _assembly = assembly;
        _intervalTicks = (long)(Interval.TotalSeconds * clock.TimestampFrequency);
        _due = clock.GetTimestamp();
        _stopped = _stopping.Token;
    }

    /// <summary>
    /// The snapshot finished since the last call, if there is one, and the next one started if it
    /// is due and none is being assembled. Called from the scan's progress report.
    ///
    /// <para>A finished snapshot waits for the next report rather than making one of its own, so
    /// every report still comes from the scan, one at a time and with its counts in order, and none
    /// can come after the scan has returned.</para>
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

            // A snapshot that failed is a defect, and it fails the scan rather than leaving every
            // snapshot after it silently missing.
            assembling.GetAwaiter().GetResult();
            _assembling = null;
        }

        var now = _clock.GetTimestamp();
        if (now < _due)
        {
            return ready;
        }

        var assemble = _capture(_assembly.Order());
        _started = now;
        _assembling = _assembly.Start(() => Assemble(assemble));

        return ready;
    }

    /// <summary>
    /// Start no more, stop the snapshot being assembled, if any, and return once it has stopped.
    /// The scan has ended or been cancelled, so nothing will draw it: the finished tree replaces it,
    /// or nothing does.
    /// </summary>
    public void Stop()
    {
        _stopping.Cancel();

        try
        {
            _assembling?.GetAwaiter().GetResult();
        }
        finally
        {
            _stopping.Dispose();
        }
    }

    private void Assemble(Func<CancellationToken, ExploreTree?> assemble)
    {
        ExploreTree? tree;

        try
        {
            tree = assemble(_stopped);
        }
        catch (OperationCanceledException) when (_stopped.IsCancellationRequested)
        {
            // Stopped by Stop, which is waiting for exactly this, and not a failure of the scan.
            return;
        }

        var ended = _clock.GetTimestamp();

        // Read by the scan only once it has seen this finish, so the task's completion publishes it.
        // An assembling with nothing to draw is spaced like any other, because it cost the same pass.
        _due = ended + Math.Max(_intervalTicks, (ended - _started) * AssemblingTimes);
        Volatile.Write(ref _ready, tree);
    }
}
