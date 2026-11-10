namespace Deguffer.Core.Exploring;

/// <summary>
/// Hands back to Windows the memory that a scan's snapshots left behind, once the scan has ended.
///
/// <para>Each snapshot of a scan in progress allocates columns on the large object heap: every column
/// on the file table's route, about 51 bytes a node, and on a walk's those it totals, about 30
/// (<see cref="SnapshotColumns"/>). Every snapshot the next one replaces is garbage, and the
/// workstation collector gives the space back to Windows only during later collections. A page left
/// idle after a scan allocates nothing and runs none, so the peak stayed committed for as long as
/// the window was open. Measured after a walk of a system drive of 2.84 million entries, with every
/// column copied for each snapshot: 977 MB committed five seconds after the scan, 964 MB after a full
/// collection, and 815 MB, of which 236 to 245 MB was live, ten minutes after another such scan
/// (#738).</para>
///
/// <para>One aggressive collection compacts every generation, the large object heap with them, and
/// decommits what it frees. Measured once, at the end of such a scan, it took committed memory from
/// 946 MB to 338 MB for a pause of 157 ms. That pause is why this runs once, after a scan, when
/// nothing is moving, rather than on any cadence. The collector's settings <c>ConserveMemory</c> and
/// <c>RetainVM</c> do not help: the first only changes when the collector compacts, and the second
/// is already off.</para>
/// </summary>
public static class SpentSnapshots
{
    /// <summary>
    /// Collect and compact every generation and decommit what is freed, blocking every managed
    /// thread until it is done. Call it from a caller that is idle, never on a frame being drawn.
    /// </summary>
    public static void Release() =>
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
}
