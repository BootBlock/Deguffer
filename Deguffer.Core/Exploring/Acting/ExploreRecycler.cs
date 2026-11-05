using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// Moves the items of one Explore removal to the Recycle Bin, a batch to each shell operation
/// (<see cref="IRecycleBin.LargestBatch"/>), each item looked at immediately before its batch goes
/// (<see cref="RecycleBinLook"/>).
///
/// <para><b>A folder closes its batch.</b> Its look is a walk of everything inside it, and the shell
/// moves it whole, so the walk is the last thing taken before the batch goes: nothing else is looked
/// at between the walk and the move, and the shell moves only the batch's files before it.</para>
///
/// <para><b>A cancel stops the batch at its next item</b>, which the bin answers as not reached. An
/// item not reached is left out of the outcomes, as one after the batch is, and the run is reported
/// as stopped.</para>
/// </summary>
internal static class ExploreRecycler
{
    /// <summary>
    /// Moves each of <paramref name="allowed"/> the look lets go to the bin, adding to
    /// <paramref name="outcomes"/> what became of each item reached, in the order of
    /// <paramref name="allowed"/>, however the run ends. Answers whether a cancel stopped it.
    /// </summary>
    /// <param name="allowed">Items <see cref="ExploreRemover.Partition"/> allowed, so their paths are normalised.</param>
    public static async Task<bool> RecycleAsync(
        IReadOnlyList<ExploreItem> allowed,
        IRecycleBin bin,
        IFileSystem fs,
        List<ExploreItemOutcome> outcomes,
        CancellationToken ct)
    {
        var reached = new ExploreItemOutcome?[allowed.Count];
        List<int> batch = [];

        try
        {
            for (var i = 0; i < allowed.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (await RecycleBinLook.WhyItStaysAsync(allowed[i], fs, ct).ConfigureAwait(false) is { } stays)
                {
                    reached[i] = new ExploreItemOutcome(allowed[i].Path, Removed: false, Bytes: 0, stays);
                    continue;
                }

                batch.Add(i);

                if ((allowed[i].IsDirectory || batch.Count == IRecycleBin.LargestBatch)
                    && Recycle(allowed, batch, bin, reached, ct))
                {
                    return true;
                }
            }

            return batch.Count > 0 && Recycle(allowed, batch, bin, reached, ct);
        }
        finally
        {
            outcomes.AddRange(reached.OfType<ExploreItemOutcome>());
        }
    }

    /// <summary>
    /// Hands <paramref name="batch"/> to the bin, records in <paramref name="reached"/> what became of
    /// each item the bin reached, and empties the batch. Answers whether a cancel stopped it.
    /// </summary>
    private static bool Recycle(
        IReadOnlyList<ExploreItem> allowed,
        List<int> batch,
        IRecycleBin bin,
        ExploreItemOutcome?[] reached,
        CancellationToken ct)
    {
        var recycled = bin.Recycle([.. batch.Select(index => ExploreRemover.BinPath(allowed[index]))], ct);
        var stopped = false;

        for (var i = 0; i < batch.Count; i++)
        {
            var (item, outcome) = (allowed[batch[i]], recycled[i]);

            if (outcome.Cancelled)
            {
                stopped = true;
                continue;
            }

            reached[batch[i]] = new ExploreItemOutcome(
                item.Path,
                outcome.Removed,
                outcome.Removed ? item.Bytes : 0,
                outcome.Message ?? "Moved to the Recycle Bin.")
            {
                DeletedOutright = outcome.DeletedOutright,
                LowerBound = outcome.Removed && item.LowerBound,
            };
        }

        batch.Clear();
        return stopped;
    }
}
