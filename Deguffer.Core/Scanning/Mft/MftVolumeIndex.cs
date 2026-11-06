using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A whole volume's directory tree and file sizes, answering questions about paths.
///
/// This is what makes §5.5 work: the expensive part happens once in
/// <see cref="MftVolumeIndexBuilder"/>, and every subsequent question — "how big is the npm cache",
/// "how big is `.gradle\caches`" — is a lookup rather than another walk. Building it costs seconds
/// where enumerating a handful of profile subtrees exceeded ten minutes during the audit.
///
/// Holds no knowledge of providers, tiers or caches; it answers questions about paths and sizes,
/// and nothing above it can tell which route produced an answer.
/// </summary>
public sealed class MftVolumeIndex(MftVolumeTree tree, MftChildLinks links)
{
    /// <summary>No node left out, for a measurement of the whole tree.</summary>
    internal static readonly IReadOnlySet<uint> NothingLeftOut = new HashSet<uint>();

    private readonly MftSubtreeTally _tally = new(tree, links);

    /// <summary>
    /// Total the subtree rooted at <paramref name="relativePath"/>, given as components below the
    /// volume root. Returns null when the path is not in the index, which the caller must treat as
    /// "ask the slow path" rather than "zero" — a path that exists but was missed would otherwise
    /// silently report an empty cache.
    ///
    /// Null is also the answer when the table described the path but not the size of everything
    /// under it. Both cases mean the same thing to the caller, and neither can be answered with a
    /// number.
    /// </summary>
    /// <param name="keep">
    /// Files the user has asked to be left alone because they were touched recently. They are
    /// stepped over rather than summed, so that this total and the deletion that follows it
    /// describe the same set of files.
    /// </param>
    /// <param name="withheldRecent">
    /// Whether <paramref name="keep"/> left at least one real file out of the total. Reported
    /// because a zero is otherwise ambiguous — see <see cref="ScanResult.WithheldRecent"/> for the
    /// claim that rests on telling an empty location from a wholly recent one.
    /// </param>
    public ScanSize? TryMeasure(
        IReadOnlyList<string> relativePath,
        MinimumAge keep,
        out bool withheldRecent) =>
        TryMeasure(relativePath, keep, out withheldRecent, out _);

    /// <inheritdoc cref="TryMeasure(IReadOnlyList{string}, MinimumAge, out bool)"/>
    /// <param name="mailStores">
    /// The Outlook mail stores the subtree holds, each as components below the volume root, and none
    /// of them in the total. See <see cref="MailStore"/>.
    /// </param>
    public ScanSize? TryMeasure(
        IReadOnlyList<string> relativePath,
        MinimumAge keep,
        out bool withheldRecent,
        out IReadOnlyList<IReadOnlyList<string>> mailStores) =>
        TryMeasure(relativePath, keep, keepRoot: false, NothingLeftOut, out withheldRecent, out mailStores);

    /// <inheritdoc cref="TryMeasure(IReadOnlyList{string}, MinimumAge, out bool, out IReadOnlyList{IReadOnlyList{string}})"/>
    /// <param name="keepRoot">
    /// Whether the directory measured stays standing, so its own entry is never counted. See
    /// <see cref="ScanBounds.KeepRoot"/>.
    /// </param>
    /// <param name="leftOut">
    /// Nodes below the path that are neither counted nor entered, and keep every folder above them
    /// standing, as <see cref="TryPlace"/> found them. See <see cref="ScanBounds.LeftOut"/>.
    /// </param>
    public ScanSize? TryMeasure(
        IReadOnlyList<string> relativePath,
        MinimumAge keep,
        bool keepRoot,
        IReadOnlySet<uint> leftOut,
        out bool withheldRecent,
        out IReadOnlyList<IReadOnlyList<string>> mailStores)
    {
        if (TryMeasure(relativePath, [keep], keepRoot, leftOut) is not { } measured)
        {
            withheldRecent = false;
            mailStores = [];
            return null;
        }

        var reading = measured.Readings[0];

        withheldRecent = reading.WithheldRecent;
        mailStores = measured.MailStores;
        return reading.Size;
    }

    /// <summary>
    /// The same total under each of <paramref name="keeps"/> at once, from one resolution of the path
    /// and one pass over its subtree, as the walk answers every guard from one listing. Null where the
    /// table cannot answer for every guard, which is the same null as above: the readings share every
    /// rule but the guard, so whatever declines one declines them all.
    /// </summary>
    /// <param name="keeps">The guards, at most 32 of them, and at least one.</param>
    /// <param name="keepRoot">
    /// Whether the directory measured stays standing, so its own entry is never counted. See
    /// <see cref="ScanBounds.KeepRoot"/>.
    /// </param>
    /// <param name="leftOut">
    /// Nodes below the path that are neither counted nor entered, and keep every folder above them
    /// standing, as <see cref="TryPlace"/> found them. See <see cref="ScanBounds.LeftOut"/>.
    /// </param>
    public MftMeasurement? TryMeasure(
        IReadOnlyList<string> relativePath,
        IReadOnlyList<MinimumAge> keeps,
        bool keepRoot,
        IReadOnlySet<uint> leftOut) =>
        TryResolve(relativePath) is { } record ? _tally.TryTally(record, keeps, keepRoot, leftOut) : null;

    /// <summary>The same, where the caller has no guard and so nothing to be told about.</summary>
    public ScanSize? TryMeasure(IReadOnlyList<string> relativePath) =>
        TryMeasure(relativePath, MinimumAge.Off, out _);

    /// <summary>
    /// The age of the directory at <paramref name="relativePath"/> one level down, by
    /// <see cref="OneLevelAge"/>'s rule, from its own record and every child the walk would list there:
    /// each file once for each of its names in the directory, and each link by its own record. A file,
    /// a path the index does not hold, and a path through a link have no age here, as
    /// <see cref="TryMeasure(IReadOnlyList{string})"/> has no size for the last two.
    /// </summary>
    /// <returns>A FILETIME, or <see cref="OneLevelAge.Unknown"/>.</returns>
    public long AgeOf(IReadOnlyList<string> relativePath)
    {
        if (TryResolve(relativePath) is not { } record || !tree.IsDirectory(record))
        {
            return OneLevelAge.Unknown;
        }

        var newest = tree.NewestOf(record);

        for (var i = links.Start[record]; i < links.Start[record + 1]; i++)
        {
            newest = OneLevelAge.With(newest, tree.NewestOf(links.Children[i]));
        }

        return newest;
    }

    /// <summary>
    /// The node of each entry of <paramref name="leftOut"/>, given as components below
    /// <paramref name="root"/>, for <see cref="TryMeasure(IReadOnlyList{string}, MinimumAge, bool, IReadOnlySet{uint}, out bool, out IReadOnlyList{IReadOnlyList{string}})"/>
    /// to leave out, or null where one cannot be placed. See <see cref="MftLeftOutPlacement"/>.
    /// </summary>
    /// <param name="describe">
    /// How the entry at a path, given as components below the volume root, is described from the
    /// disk.
    /// </param>
    public IReadOnlySet<uint>? TryPlace(
        IReadOnlyList<string> root,
        IReadOnlyList<IReadOnlyList<string>> leftOut,
        Func<IReadOnlyList<string>, FileReading> describe) =>
        new MftLeftOutPlacement(tree, links, TryResolve).TryPlace(root, leftOut, describe);

    /// <summary>
    /// Every directory <paramref name="search"/> finds below the directory at
    /// <paramref name="root"/>, as components below that root, in no particular order. Null when
    /// the root is not in the index, or is reached through a link: either way the caller has to walk
    /// it, and an empty list would tell it there is nothing there.
    ///
    /// <para>One depth-first pass over the root's own subtree, through the child links, for every
    /// name at once. The pass stops where the walk stops: at a directory it finds, at one it never
    /// enters, at one <see cref="DirectorySearch.HandedOff"/> names, and at a link, which the walk
    /// does not list as a directory at all. A path is built
    /// only for a directory that is found, from the names the pass already holds, so nothing outside
    /// the root is read and no path is rebuilt from a parent chain.</para>
    ///
    /// <para>Only directories are considered, which needs no filtering: <see cref="MftVolumeTree.DirectoryName"/>
    /// names directories alone.</para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>>? TryFindDirectories(
        IReadOnlyList<string> root,
        DirectorySearch search,
        CancellationToken ct = default) =>
        new MftDirectoryFinder(tree, links, TryResolve).TryFind(root, search, ct);

    private uint? TryResolve(IReadOnlyList<string> relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (tree.Count <= MftRecord.RootRecordNumber)
        {
            return null;
        }

        var current = MftRecord.RootRecordNumber;

        foreach (var component in relativePath)
        {
            // A path reached through a link is one this table cannot total: whatever the link
            // stands for keeps its own place, so the subtree here is empty and would report a
            // populated cache as clear. The walk follows the link and is right, so the answer is to
            // send the caller there. Tested at every level rather than only the last, so that the
            // rule holds whatever the table turns out to contain.
            if (tree.IsReparsePoint(current)
                || !tree.IsDirectory(current)
                || TryFindChild(current, component) is not { } next)
            {
                return null;
            }

            current = next;
        }

        return tree.IsReparsePoint(current) ? null : current;
    }

    /// <summary>
    /// A linear scan of one directory's children. Directories hold tens to low thousands of
    /// entries and Deguffer resolves a handful of paths per run, so an index keyed by name would
    /// cost more to build across the whole volume than these scans ever save.
    ///
    /// <para>An exact match wins over one that differs only in case, and the fallback is what keeps
    /// a path given in the wrong case measurable. NTFS has held per-directory case sensitivity
    /// since Windows 10 1803, and WSL sets it on the trees it creates, so a directory really can
    /// hold <c>Cache</c> and <c>cache</c> at once — the same shape
    /// <see cref="Deguffer.Core.Exploring.ExploreTree.ChildrenOf"/>'s comparer breaks ties for.
    /// Child links are in record order, so taking the first case-insensitive match returns
    /// whichever of the two was created first. The number that comes back is then a total of the
    /// sibling, reported for the path the caller named — and that path is the one the user approves
    /// a deletion against.</para>
    /// </summary>
    private uint? TryFindChild(uint directory, string name)
    {
        uint? differingInCase = null;

        for (var i = links.Start[directory]; i < links.Start[directory + 1]; i++)
        {
            var child = links.Children[i];

            // Only directories carry names, so a file component never matches. Deguffer does measure
            // one file — C:\Windows\MEMORY.DMP — and this is why that measurement takes the walk:
            // naming every file on a volume would cost a string per record, which is a poor trade
            // for a single stat. See ScanStrategy.DirectRead, which is how the walk reports it.
            if (tree.DirectoryName(child) is not { } candidate)
            {
                continue;
            }

            if (candidate.Equals(name, StringComparison.Ordinal))
            {
                return child;
            }

            if (differingInCase is null && candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                differingInCase = child;
            }
        }

        return differingInCase;
    }
}
