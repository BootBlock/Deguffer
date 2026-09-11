using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <param name="Refused">What Windows still refuses across the places asked about.</param>
/// <param name="Places">Each place that still held something refused, with what it held.</param>
internal sealed record RefusalFinding(Refusals Refused, IReadOnlyList<(string Place, Refusals Refused)> Places);

/// <summary>
/// What Windows would still refuse, of the places a previous clean of one step found it refusing.
///
/// <para><b>Asked on the removal's own terms.</b> The places are walked by <see cref="RemovalWalk"/>,
/// under the plan's guard and the step's spared entries, and each file the removal would attempt is
/// opened for deletion. So the bytes found are bytes the step's estimate counted and the removal
/// would try to take — taking them out of that estimate subtracts like from like, which a second
/// measurement under different rules would not.</para>
/// </summary>
internal static class RefusalCheck
{
    /// <param name="recorded">
    /// The places <see cref="RefusalRecord"/> holds for <paramref name="step"/>, in display form.
    /// </param>
    public static RefusalFinding Of(
        DeleteStep step,
        IReadOnlyList<string> recorded,
        MinimumAge keep,
        IFileSystem fs,
        CancellationToken ct)
    {
        var bounds = new RemovalBounds(KeepRoot: false, step is ClearDirectoryStep clear ? clear.Spared : []);

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 2, 16),
        };

        var refused = Refusals.None;
        var places = new List<(string, Refusals)>();

        foreach (var place in recorded)
        {
            ct.ThrowIfCancellationRequested();

            var extended = LongPath.Extended(place);

            // The record is read from a file on the user's disk rather than derived here, so it is
            // not trusted to name a place inside this step: one outside it would be opened for
            // deletion and then taken out of a figure it was never part of. A spared entry is already
            // out of that figure, and the removal will not enter it.
            if (!LongPath.Contains(step.Path, place) || bounds.SparedPaths.Contains(extended))
            {
                continue;
            }

            var found = Check(extended, keep, bounds, fs, options, ct);

            if (!found.IsEmpty)
            {
                refused += found;
                places.Add((place, found));
            }
        }

        return new RefusalFinding(refused, places);
    }

    private static Refusals Check(
        string extended,
        MinimumAge keep,
        RemovalBounds bounds,
        IFileSystem fs,
        ParallelOptions options,
        CancellationToken ct)
    {
        // A link is removed as a link and holds nothing the estimate counted.
        if (fs.IsReparsePoint(extended))
        {
            return Refusals.None;
        }

        if (fs.DirectoryExists(extended))
        {
            var inventory = RemovalWalk.Gather(extended, keep, bounds, fs, ct);
            var counter = new RefusalCounter();

            Parallel.ForEach(inventory.Files, options, file =>
            {
                if (fs.ProbeRemoval(file.Path) is { } reason)
                {
                    counter.Add(reason, file.Length);
                }
            });

            return counter.Total;
        }

        // A file, or nothing at all. The guard is asked before the probe, because a file it protects
        // is already out of the estimate and is not one the removal would attempt.
        if (fs.TryGetFileLength(extended) is not { } length
            || (fs.TryGetNewestFileTime(extended) is { } newest && keep.Protects(newest)))
        {
            return Refusals.None;
        }

        return fs.ProbeRemoval(extended) is { } refusal ? Refusals.One(refusal, length) : Refusals.None;
    }
}
