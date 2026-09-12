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
        out IReadOnlyList<IReadOnlyList<string>> mailStores)
    {
        withheldRecent = false;
        mailStores = [];

        if (TryResolve(relativePath) is not { } record)
        {
            return null;
        }

        if (tree.IsDirectory[record])
        {
            return SumSubtree(record, keep, out withheldRecent, out mailStores);
        }

        if (tree.SizeUnknown[record])
        {
            return null;
        }

        if (!keep.Protects(tree.Newest[record]))
        {
            return new ScanSize(tree.Allocated[record], tree.Logical[record], Entries: 1);
        }

        withheldRecent = true;
        return ScanSize.Zero;
    }

    /// <summary>The same, where the caller has no guard and so nothing to be told about.</summary>
    public ScanSize? TryMeasure(IReadOnlyList<string> relativePath) =>
        TryMeasure(relativePath, MinimumAge.Off, out _);

    /// <summary>
    /// Every directory on the volume called <paramref name="name"/>, as components below the volume
    /// root.
    ///
    /// A linear pass rather than a lookup, and deliberately so. <see cref="TryFindChild"/> argues
    /// against keeping an index keyed by name, and that argument still holds: this walks the array
    /// once per query, costs nothing to build, and is asked for a single name once per planning
    /// pass. Building a volume-wide name index to serve that would cost more than it saves.
    ///
    /// Only directories are considered, which needs no filtering — <see cref="MftVolumeTree.Names"/>
    /// is populated for directories alone.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> FindDirectoriesNamed(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var matches = new List<IReadOnlyList<string>>();

        for (uint record = 0; record < tree.Count; record++)
        {
            // The table runs to millions of records, so the cancellation check is amortised rather
            // than paid per entry.
            if ((record & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (tree.Names[record] is { } candidate
                && candidate.Equals(name, StringComparison.OrdinalIgnoreCase)
                && TryBuildPath(record) is { } components)
            {
                matches.Add(components);
            }
        }

        return matches;
    }

    /// <summary>
    /// Rebuild one record's path by walking <see cref="MftVolumeTree.Parent"/> up to the root.
    ///
    /// Returns null for a record whose chain does not reach the root — an orphan, or a parent
    /// pointing outside the table — and for one whose chain passes through a link. The first is a
    /// real condition on a live volume rather than a corruption check: a directory deleted while the
    /// table was being read leaves its children briefly unreachable. Either way a path that cannot
    /// be rebuilt must be dropped rather than guessed at, because a wrong path here is a wrong
    /// deletion target.
    /// </summary>
    private IReadOnlyList<string>? TryBuildPath(uint record)
    {
        // Deep enough for any real tree, and bounded so a cyclic parent chain terminates instead of
        // hanging the scan. NTFS itself cannot express a path with this many components.
        const int MaximumDepth = 512;

        var components = new List<string>();
        var current = record;

        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            if (current == MftRecord.RootRecordNumber)
            {
                components.Reverse();
                return components;
            }

            if (current >= tree.Count)
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
            if ((tree.IsReparsePoint[current] && !(depth == 0 && tree.IsMailStore[current]))
                || (tree.Names[current] ?? tree.MailStoreNames.GetValueOrDefault(current)) is not { } component)
            {
                return null;
            }

            components.Add(component);

            var parent = tree.Parent[current];

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
            if (tree.IsReparsePoint[current]
                || !tree.IsDirectory[current]
                || TryFindChild(current, component) is not { } next)
            {
                return null;
            }

            current = next;
        }

        return tree.IsReparsePoint[current] ? null : current;
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
            if (tree.Names[child] is not { } candidate)
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
    /// </summary>
    private ScanSize? SumSubtree(
        uint root,
        MinimumAge keep,
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

            // Before everything else, as the removal asks it: the store stays whatever its age and
            // whatever reparse point it carries. The removal does not read which kind of mark a file
            // has, so a store the table calls a link is still left, and is named here to agree.
            if (tree.IsMailStore[node])
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
            if (tree.IsReparsePoint[node])
            {
                if (tree.IsDirectory[node])
                {
                    CountOrKeep(node, item.Holder);
                }

                continue;
            }

            if (tree.SizeUnknown[node])
            {
                withheldRecent = false;
                return null;
            }

            // The guard never prunes the traversal, and a folder is never kept by its own timestamp:
            // NTFS moves a directory's timestamp whenever an entry is added, removed or renamed, so a
            // `continue` here would drop a whole subtree of stale files because one recent thing was
            // written beside them. The guard is about files, and a folder stays only while something
            // inside it does.
            if (tree.IsDirectory[node])
            {
                var position = folders.Count;
                folders.Add((item.Holder, false));

                for (var i = links.Start[node]; i < links.Start[node + 1]; i++)
                {
                    stack.Push((links.Children[i], position));
                }

                continue;
            }

            if (CountOrKeep(node, item.Holder))
            {
                allocated += tree.Allocated[node];
                logical += tree.Logical[node];
            }
        }

        withheldRecent = kept;
        mailStores = stores;

        return new ScanSize(allocated, logical, Entries: entries + folders.Count(folder => !folder.Stays));

        // Whether the removal takes this entry. One it leaves keeps every folder above it standing.
        bool CountOrKeep(uint node, int holder)
        {
            if (!keep.Protects(tree.Newest[node]))
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
