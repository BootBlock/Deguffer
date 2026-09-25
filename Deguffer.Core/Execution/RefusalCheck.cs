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
/// <param name="Undescribed">
/// What Windows would not describe, in display form: the step's own path, which stands in for every
/// recorded place beneath it, or a recorded place. Whether what is there still refuses is unknown.
/// Nothing is taken out of the estimate for them, because nothing was measured, and they are not
/// among <paramref name="Places"/>, because "still refused" is a claim nobody established. See
/// <see cref="PathPresence.Refused"/>.
/// </param>
internal sealed record RefusalFinding(
    Refusals Refused,
    IReadOnlyList<(string Place, Refusals Refused)> Places,
    long Standing,
    IReadOnlyList<string> Undescribed);

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
    private static readonly RefusalFinding Nothing = new(Refusals.None, [], Standing: 0, Undescribed: []);

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
        if (LongPath.Configured(step.Path) is not { } root)
        {
            return Nothing;
        }

        var extendedRoot = LongPath.Extended(root);

        // Settled before the link question, which fails closed: a root Windows would not describe
        // answers "a link" there, which reads as "nothing here will be refused". Nothing beneath it
        // can be asked about either, so the finding says that rather than a clean answer.
        if (fs.ProbeDirectory(extendedRoot) is PathPresence.Refused)
        {
            return Nothing with { Undescribed = [root] };
        }

        // A root that is a link is removed as a link, or left alone where it must stay, and never
        // entered — so nothing beneath it is anything the removal would attempt. Asking about the far
        // side would open files nobody classified and take them out of a figure about somewhere else.
        if (fs.IsReparsePoint(extendedRoot))
        {
            return Nothing;
        }

        var bounds = step is ClearDirectoryStep clear
            ? new RemovalBounds(KeepRoot: false, clear.Spared, clear.OwnedElsewhere)
            : RemovalBounds.None;

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 2, 16),
        };

        var refused = Refusals.None;
        var places = new List<(string, Refusals)>();
        var undescribed = new List<string>();

        var standing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A folder cleared in place stays whatever happens, and its own entry was never in the count.
        var rootCounted = step is not ClearDirectoryStep;

        foreach (var place in PlacesOf(root, recorded))
        {
            ct.ThrowIfCancellationRequested();

            var extended = LongPath.Extended(place);

            // A spared entry is already out of the estimate, and the removal will not enter it.
            if (bounds.SparedPaths.Contains(extended) || bounds.OwnedElsewherePaths.Contains(extended))
            {
                continue;
            }

            var found = Check(extended, keep, bounds, fs, options, ct);

            if (found.Undescribed)
            {
                undescribed.Add(place);
                continue;
            }

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

        return new RefusalFinding(refused, places, standing.Count, undescribed);

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

    private static PlaceCheck Check(
        string extended,
        MinimumAge keep,
        RemovalBounds bounds,
        IFileSystem fs,
        ParallelOptions options,
        CancellationToken ct)
    {
        // Kind-agnostic, although it is the directory probe: a refusal is decided before the kind is
        // read, so a place that is a file refuses here too. Asked first for the reason the root is.
        var presence = fs.ProbeDirectory(extended);

        if (presence is PathPresence.Refused)
        {
            return PlaceCheck.CouldNotAsk;
        }

        // A link is removed as a link and holds nothing the estimate counted.
        if (fs.IsReparsePoint(extended))
        {
            return PlaceCheck.Clear;
        }

        if (presence is PathPresence.Present)
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

            return new PlaceCheck(counter.Total, files);
        }

        // A file, or nothing at all. The guard is asked before the probe, because a file it protects
        // is already out of the estimate and is not one the removal would attempt.
        if (fs.TryGetFileLength(extended) is not { } length
            || (fs.TryGetNewestFileTime(extended) is { } newest && keep.Protects(newest)))
        {
            return PlaceCheck.Clear;
        }

        return fs.ProbeRemoval(extended) is { } refusal
            ? new PlaceCheck(Refusals.One(refusal, length), [extended])
            : PlaceCheck.Clear;
    }

    /// <summary>What is refused at one place, and the refused files themselves in extended form.</summary>
    /// <param name="Undescribed">Windows would not describe the place, so nothing was asked.</param>
    private readonly record struct PlaceCheck(Refusals Refused, IReadOnlyCollection<string> Files, bool Undescribed = false)
    {
        public static PlaceCheck Clear => new(Refusals.None, []);

        public static PlaceCheck CouldNotAsk => new(Refusals.None, [], Undescribed: true);
    }
}
