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
        withheldRecent = false;
        mailStores = [];

        if (TryResolve(relativePath) is not { } record)
        {
            return null;
        }

        if (tree.IsDirectory(record))
        {
            return SumSubtree(record, keep, keepRoot, leftOut, out withheldRecent, out mailStores);
        }

        if (tree.SizeUnknown(record))
        {
            return null;
        }

        if (!keep.Protects(tree.NewestOf(record)))
        {
            return new ScanSize(tree.AllocatedOf(record), tree.LogicalOf(record), Entries: 1);
        }

        withheldRecent = true;
        return ScanSize.Zero;
    }

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

    /// <summary>
    /// Rebuild one node's path by walking <see cref="MftVolumeTree.ParentOf"/> up to the root. A
    /// link's path is its own: its name, under the directory holding that name.
    ///
    /// Returns null for a record whose chain does not reach the root — an orphan, or a parent
    /// pointing outside the table — and for one whose chain passes through a link. The first is a
    /// real condition on a live volume rather than a corruption check: a directory deleted while the
    /// table was being read leaves its children briefly unreachable. Either way a path that cannot
    /// be rebuilt must be dropped rather than guessed at, because a wrong path here is a wrong
    /// deletion target.
    /// </summary>
    private IReadOnlyList<string>? TryBuildPath(uint node)
    {
        // Deep enough for any real tree, and bounded so a cyclic parent chain terminates instead of
        // hanging the scan. NTFS itself cannot express a path with this many components.
        const int MaximumDepth = 512;

        var components = new List<string>();
        var current = node;

        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            if (current == MftRecord.RootRecordNumber)
            {
                components.Reverse();
                return components;
            }

            if (current >= tree.NodeCount)
            {
                return null;
            }

            // A link, or anything under one. The walk never enters a reparse point, so a path that
            // passes through one is not something the guaranteed route could ever have produced —
            // and the deletion target it names is a pointer, not the thing the user asked about.
            //
            // The name is a directory's, or a mail store's at the leaf: the only file whose path the
            // table is ever asked to rebuild. A store at the leaf is the one entry allowed to carry a
            // reparse point, because it is a file no path passes through, and the removal leaves it
            // whatever mark it carries.
            if ((tree.IsReparsePoint(current) && !(depth == 0 && tree.IsMailStore(current)))
                || (tree.DirectoryName(current) ?? tree.MailStoreName(current)) is not { } component)
            {
                return null;
            }

            components.Add(component);

            var parent = tree.ParentOf(current);

            // Only the root is its own parent; anything else claiming to be would loop forever.
            if (parent == current)
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

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

    /// <summary>
    /// Iterative depth-first sum, abandoned as soon as one entry's size turns out to be unknown.
    ///
    /// Recursion would be the obvious shape and would overflow the stack on a deep node_modules
    /// tree, which is exactly the kind of tree this tool exists to measure.
    ///
    /// Abandoning the whole subtree for one entry is the same trade <see cref="MftVolumeIndexBuilder"/>
    /// makes for a table it could not fully read: a total missing one file is still a total, and
    /// nothing downstream can tell it from a correct one. The cost is that this path takes the walk;
    /// the alternative cost is a cache reported as clear when it is not.
    ///
    /// <para><b>Entries are counted on the removal's terms.</b> A folder stays while anything inside
    /// it stays, so a file the guard keeps has to mark every folder above it. Recursion would carry
    /// that upward for free; here each folder reached records the position of the folder holding it,
    /// and a kept entry walks that chain.</para>
    ///
    /// <para><b>An Outlook mail store is left out on the same terms, and named (§9).</b> It keeps its
    /// folders standing exactly as a kept file does, and its path is rebuilt so the plan can protect
    /// it. A store whose path cannot be rebuilt takes the whole subtree to the walk rather than being
    /// left out unnamed, because a store nobody can name is one nothing can prove survived.</para>
    ///
    /// <para><b>A file is counted once for each of its names in the subtree</b>, because each is a
    /// node (see <see cref="MftLink"/>). That is what the walk lists and what the removal takes, and
    /// a name outside the subtree is outside the total.</para>
    /// </summary>
    private ScanSize? SumSubtree(
        uint root,
        MinimumAge keep,
        bool keepRoot,
        IReadOnlySet<uint> leftOut,
        out bool withheldRecent,
        out IReadOnlyList<IReadOnlyList<string>> mailStores)
    {
        long allocated = 0;
        long logical = 0;
        long entries = 0;

        var kept = false;
        var folders = new List<(int Holder, bool Stays)>();
        var stores = new List<IReadOnlyList<string>>();
        mailStores = [];

        var stack = new Stack<(uint Node, int Holder)>();
        stack.Push((root, -1));

        while (stack.TryPop(out var item))
        {
            var node = item.Node;

            // First, as the removal asks whether an entry is spared first: neither counted nor
            // entered, and every folder above it stays.
            if (leftOut.Contains(node))
            {
                Stay(item.Holder);
                continue;
            }

            // Next, as the removal asks it: the store stays whatever its age and
            // whatever reparse point it carries. The removal does not read which kind of mark a file
            // has, so a store the table calls a link is still left, and is named here to agree.
            if (tree.IsMailStore(node))
            {
                if (TryBuildPath(node) is not { } components)
                {
                    withheldRecent = false;
                    return null;
                }

                stores.Add(components);
                Stay(item.Holder);
                continue;
            }

            // A link holds nothing and contributes no bytes: whatever it points at keeps its own
            // place in the table, and the walk does not enter one either. A link to a folder is still
            // an entry, removed as a link, and the walk counts it by its own timestamp; a link to a
            // file is one the walk never sees, so it is not counted here either.
            if (tree.IsReparsePoint(node))
            {
                if (tree.IsDirectory(node))
                {
                    CountOrKeep(node, item.Holder);
                }

                continue;
            }

            if (tree.SizeUnknown(node))
            {
                withheldRecent = false;
                return null;
            }

            // The guard never prunes the traversal, and a folder is never kept by its own timestamp:
            // NTFS moves a directory's timestamp whenever an entry is added, removed or renamed, so a
            // `continue` here would drop a whole subtree of stale files because one recent thing was
            // written beside them. The guard is about files, and a folder stays only while something
            // inside it does.
            if (tree.IsDirectory(node))
            {
                // A folder emptied in place is never among what its removal takes, so it starts out
                // standing, and nothing above it needs marking.
                var position = folders.Count;
                folders.Add((item.Holder, keepRoot && item.Holder < 0));

                for (var i = links.Start[node]; i < links.Start[node + 1]; i++)
                {
                    stack.Push((links.Children[i], position));
                }

                continue;
            }

            if (CountOrKeep(node, item.Holder))
            {
                allocated += tree.AllocatedOf(node);
                logical += tree.LogicalOf(node);
            }
        }

        withheldRecent = kept;
        mailStores = stores;

        return new ScanSize(allocated, logical, Entries: entries + folders.Count(folder => !folder.Stays));

        // Whether the removal takes this entry. One it leaves keeps every folder above it standing.
        bool CountOrKeep(uint node, int holder)
        {
            if (!keep.Protects(tree.NewestOf(node)))
            {
                entries++;
                return true;
            }

            kept = true;
            Stay(holder);

            return false;
        }

        // Mark the folder holding an entry that stays, and every folder above it.
        void Stay(int holder)
        {
            for (var position = holder; position >= 0 && !folders[position].Stays; position = folders[position].Holder)
            {
                folders[position] = (folders[position].Holder, true);
            }
        }
    }
}
