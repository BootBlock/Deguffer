namespace Deguffer.Core.Exploring;

/// <summary>
/// What the Explore page scans through. <see cref="ExploreScanner"/> is the one the app runs with.
///
/// <para>A seam because the page's handling of a scan turns on when things arrive: a snapshot
/// landing while the reader has something picked, a finished tree replacing the snapshots,
/// a cancellation part way through. A real walk publishes snapshots on a clock, so a test cannot
/// place one; a fake that publishes on command can.</para>
/// </summary>
public interface IExploreScanner
{
    /// <summary>
    /// Scan everything at or below <paramref name="root"/>, which is a volume root or any folder
    /// under one. <paramref name="progress"/> receives running counts, and occasionally a snapshot
    /// of the tree so far.
    /// </summary>
    /// <param name="snapshotOrder">
    /// The order each snapshot's children are to be in, asked as each one is taken, so a change of
    /// view while the scan runs reaches the next one. By name where null. See
    /// <see cref="Rendering.ExploreSurface.SnapshotOrder"/>.
    /// </param>
    ValueTask<ExploreScan> ScanAsync(
        string root,
        IProgress<ExploreProgress>? progress = null,
        Func<ExploreChildOrder>? snapshotOrder = null,
        CancellationToken ct = default);
}
