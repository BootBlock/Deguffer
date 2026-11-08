using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// Work on each of a list of paths that waits on the disk, a few attribute reads, a listing or a small
/// file each, done several at once and handed back in the list's order.
///
/// <para><b>Bounded by the disk's own number</b> (G4). One after another, a list of several hundred
/// paths paid each wait in turn. Here the list is worked by as many threads as a walk runs on
/// whichever volume asked about is tuned for the most (<see cref="WalkTuning.Threads"/>), and each
/// item takes a slot in <see cref="ListingBudget.Shared"/> under its own volume's number, so the work
/// shares a disk with the walks of the rows planned beside it rather than adding to them. A spinning
/// disk tuned to one thread is still read one path at a time.</para>
///
/// <para><b>In the list's order</b>, whatever order the items finish in, so a caller that sorts the
/// answers into lists keeps the order its own search gave them.</para>
///
/// <para>On threads of the list's own rather than the pool's (<see cref="ListWorkers"/>), with the
/// calling thread working too. An async caller uses <see cref="EachAsync{T}"/>, which starts the run on
/// <see cref="WorkerThreads.RunAsync{T}(Func{T}, CancellationToken)"/>.</para>
///
/// <para>The work must hold no state it shares with another item, and must take no slot of the budget
/// itself: a slot is held for the whole item, so an item waiting for a second one could wait for
/// itself.</para>
/// </summary>
internal static class DiskBoundWork
{
    /// <summary>
    /// <paramref name="work"/> applied to each of <paramref name="paths"/>, on a thread of the walk's
    /// own, in their order.
    /// </summary>
    /// <param name="tuning">What the walks of each volume run with. See <see cref="IDirectoryScanner.Tuning"/>.</param>
    public static Task<IReadOnlyList<T>> EachAsync<T>(
        IReadOnlyList<string> paths,
        ScanTuner tuning,
        Func<string, T> work,
        CancellationToken ct) =>
        paths.Count == 0
            ? Task.FromResult<IReadOnlyList<T>>([])
            : WorkerThreads.Shared.RunAsync<IReadOnlyList<T>>(() => Each(paths, tuning, ListingBudget.Shared, work, ct), ct);

    /// <summary><paramref name="work"/> applied to each of <paramref name="paths"/> on this thread and its helpers, in their order.</summary>
    /// <param name="tuning">What the walks of each volume run with. See <see cref="IDirectoryScanner.Tuning"/>.</param>
    /// <param name="budget">The budget every item takes its slot in: the seam a test holds a volume's slots through.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal static T[] Each<T>(
        IReadOnlyList<string> paths,
        ScanTuner tuning,
        ListingBudget budget,
        Func<string, T> work,
        CancellationToken ct)
    {
        if (paths.Count == 0)
        {
            return [];
        }

        var limits = new int[paths.Count];
        var byVolume = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < paths.Count; index++)
        {
            var root = Path.GetPathRoot(paths[index]) ?? paths[index];

            if (!byVolume.TryGetValue(root, out var limit))
            {
                limit = tuning.For(paths[index]).Walk.Threads;
                byVolume[root] = limit;
            }

            limits[index] = limit;
        }

        var results = new T[paths.Count];

        ListWorkers.Run(
            paths.Count,
            byVolume.Values.Max(),
            index =>
            {
                // Extended, as a walk's listings are, so both count against the same volume.
                using var slot = budget.Take(LongPath.Extended(paths[index]), limits[index]);
                results[index] = work(paths[index]);
            },
            ct);

        return results;
    }
}
