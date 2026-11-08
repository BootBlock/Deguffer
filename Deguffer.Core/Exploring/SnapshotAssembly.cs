namespace Deguffer.Core.Exploring;

/// <summary>
/// How a scan in progress makes its snapshots: where each is assembled, and the order each one's
/// children are put in. See <see cref="ScanSnapshots"/> for when one is taken.
/// </summary>
/// <param name="Start">
/// Runs one assembling and says when it has finished: <see cref="ScanSnapshots.InBackground"/> outside
/// tests.
/// </param>
/// <param name="Order">
/// Asked as each snapshot is captured, on the scan's reporting thread, so a reader who changes the
/// view while the scan runs gets the next snapshot in the order that view needs. See
/// <see cref="Rendering.ExploreSurface.SnapshotOrder"/>.
/// </param>
internal sealed record SnapshotAssembly(Func<Action, Task> Start, Func<ExploreChildOrder> Order)
{
    /// <summary>
    /// Snapshots assembled on the thread pool and ordered by name, for a caller with no picture that
    /// needs another order.
    /// </summary>
    public static SnapshotAssembly ByName { get; } = new(ScanSnapshots.InBackground, static () => ExploreChildOrder.ByName);
}
