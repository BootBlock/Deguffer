using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <param name="Refused">What Windows still refuses across the places asked about.</param>
/// <param name="Places">Each place that still held something refused, with what it held.</param>
/// <param name="Standing">
/// How many of the entries the step's estimate counted stay because of what is refused: each refused
/// file, and every folder above one up to the step's own path, each counted once. A folder still
/// holding something cannot be removed, so a refused file keeps all of them.
/// </param>
internal sealed record RefusalFinding(
    Refusals Refused,
    IReadOnlyList<(string Place, Refusals Refused)> Places,
    long Standing);

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
    private static readonly RefusalFinding Nothing = new(Refusals.None, [], Standing: 0);

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
        // A root that is a link is removed as a link, or left alone where it must stay, and never
        // entered — so nothing beneath it is anything the removal would attempt. Asking about the far
        // side would open files nobody classified and take them out of a figure about somewhere else.
        if (LongPath.Configured(step.Path) is not { } root || fs.IsReparsePoint(LongPath.Extended(root)))
        {
            return Nothing;
        }

        var bounds = new RemovalBounds(KeepRoot: false, step is ClearDirectoryStep clear ? clear.Spared : []);

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 2, 16),
        };

        var refused = Refusals.None;
        var places = new List<(string, Refusals)>();

        var extendedRoot = LongPath.Extended(root);
        var standing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A folder cleared in place stays whatever happens, and its own entry was never in the count.
        var rootCounted = step is not ClearDirectoryStep;

        foreach (var place in PlacesOf(root, recorded))
        {
            ct.ThrowIfCancellationRequested();

            var extended = LongPath.Extended(place);

            // A spared entry is already out of the estimate, and the removal will not enter it.
            if (bounds.SparedPaths.Contains(extended))
            {
                continue;
            }

            var found = Check(extended, keep, bounds, fs, options, ct);

            if (!found.Refused.IsEmpty)
            {
                refused += found.Refused;
                places.Add((place, found.Refused));
            }

            foreach (var file in found.Files)
            {
                Stand(file);
            }
        }

        return new RefusalFinding(refused, places, standing.Count);

        // The refused file, and every folder above it the removal would otherwise have taken. A folder
        // already standing has everything above it standing too, so the climb stops there.
        void Stand(string file)
        {
            standing.Add(file);

            for (var folder = Path.GetDirectoryName(file);
                 folder is not null && LongPath.Contains(extendedRoot, folder);
                 folder = Path.GetDirectoryName(folder))
            {
                if ((!rootCounted && folder.Equals(extendedRoot, StringComparison.OrdinalIgnoreCase))
                    || !standing.Add(folder))
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The recorded places that have the shape the removal records: the step's own path, or an entry
    /// directly inside it.
    ///
    /// <para>The record resolves every place it reads and keeps each once — see
    /// <see cref="RefusalRecord"/> — but it cannot know which step a place belongs under, so that is
    /// asked here. A prefix test is not enough: <c>&lt;step&gt;\x\..\..\Documents</c> starts with the
    /// step's path and resolves outside it. A place nested deeper would be counted a second time under
    /// its parent, and could sit inside a spared entry the spared set only matches by its own path.
    /// Requiring the step to be the place, or the directory holding it, closes all three — and any
    /// other spelling of a place fails it, which leaves bytes in the estimate rather than taking them
    /// out.</para>
    /// </summary>
    private static IEnumerable<string> PlacesOf(string root, IReadOnlyList<string> recorded) =>
        recorded.Where(place => place.Equals(root, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetDirectoryName(place), root, StringComparison.OrdinalIgnoreCase));

    /// <returns>What is refused at one place, and the refused files themselves in extended form.</returns>
    private static (Refusals Refused, IReadOnlyCollection<string> Files) Check(
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
            return (Refusals.None, []);
        }

        if (fs.DirectoryExists(extended))
        {
            var inventory = RemovalWalk.Gather(extended, keep, bounds, fs, ct);
            var counter = new RefusalCounter();
            var files = new ConcurrentBag<string>();

            Parallel.ForEach(inventory.Files, options, file =>
            {
                if (fs.ProbeRemoval(file.Path) is { } reason)
                {
                    counter.Add(reason, file.Length);
                    files.Add(file.Path);
                }
            });

            return (counter.Total, files);
        }

        // A file, or nothing at all. The guard is asked before the probe, because a file it protects
        // is already out of the estimate and is not one the removal would attempt.
        if (fs.TryGetFileLength(extended) is not { } length
            || (fs.TryGetNewestFileTime(extended) is { } newest && keep.Protects(newest)))
        {
            return (Refusals.None, []);
        }

        return fs.ProbeRemoval(extended) is { } refusal
            ? (Refusals.One(refusal, length), [extended])
            : (Refusals.None, []);
    }
}
