namespace Deguffer.Core.Exploring;

/// <summary>
/// Hands back to Windows the memory that a scan's snapshots left behind, once the scan has ended.
///
/// <para>Each snapshot of a scan in progress is a full set of columns, about 51 bytes a node and so
/// about 145 MB on the large object heap for a system drive of 2.8 million entries. Every snapshot
/// the next one replaces is garbage, and the workstation collector gives the space back to Windows
/// only during later collections. A page left idle after a scan allocates nothing and runs none, so
/// the peak stayed committed for as long as the window was open: measured on a system drive, 977 MB
/// committed against 236 to 245 MB live, ten minutes after the scan, and a full collection that did
/// not compact left it at 964 MB (#738).</para>
///
/// <para>One aggressive collection compacts every generation, the large object heap with them, and
/// decommits what it frees. Measured once, it took the same scan from 946 MB to 338 MB committed
/// for a pause of 157 ms. That pause is why this runs once, after a scan, when nothing is moving,
/// rather than on any cadence. The collector's settings <c>ConserveMemory</c> and <c>RetainVM</c>
/// do not help: the first only changes when the collector
/// compacts, and the second is already off.</para>
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
