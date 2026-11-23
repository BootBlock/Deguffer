using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Cloud;

/// <summary>
/// Every sync root to be walked, put into walks as a scan's directories are (<see cref="DirectoryWalks"/>,
/// #1071): the roots on one set of disks share one <see cref="PlaceholderWalk"/> for each tuning, and
/// the walks on separate disks run at the same time.
///
/// <para><b>Bounded by the walk, not by a listing budget.</b> The placeholder walk takes no slot of
/// <see cref="ListingBudget"/>, so a walk for each of two roots on one disk, run at once, would read it
/// with twice the threads it is tuned for. Sharing one walk, the roots share its threads, and the
/// second root costs no walk's start and tail of its own. Roots on separate disks cost the longer walk
/// rather than the sum. Where a root's disks are not known, every root is in one set, and the walks of
/// that set run one after another.</para>
/// </summary>
internal static class PlaceholderWalks
{
    /// <summary>
    /// What each of <paramref name="syncRoots"/> holds, in the order given. Asks what each root is on,
    /// which opens a device the first time a volume is met, on a worker thread rather than the caller's.
    /// </summary>
    /// <param name="tuning">What the walks of each kind of drive run with.</param>
    public static async Task<IReadOnlyList<ReleaseSelection>> OfAsync(
        ICloudFiles cloud, IReadOnlyList<string> syncRoots, MinimumAge keep, ScanTuner tuning, CancellationToken ct)
    {
        if (syncRoots.Count == 0)
        {
            return [];
        }

        var walks = await WorkerThreads.Shared.RunAsync(() => DirectoryWalks.Plan(syncRoots, tuning), ct).ConfigureAwait(false);
        var selections = new ReleaseSelection[syncRoots.Count];

        // Each walk writes the slots of its own roots, which no other walk writes.
        await DirectoryWalks.RunAsync(
                walks,
                (walk, stop) =>
                {
                    var found = PlaceholderWalk.Of(
                        cloud, [.. walk.Indexes.Select(at => syncRoots[at])], keep, walk.Tuning.Threads, stop);

                    for (var i = 0; i < found.Count; i++)
                    {
                        selections[walk.Indexes[i]] = found[i];
                    }
                },
                ct)
            .ConfigureAwait(false);

        return selections;
    }
}
