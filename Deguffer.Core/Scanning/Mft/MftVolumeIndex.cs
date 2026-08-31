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
    public ScanSize? TryMeasure(IReadOnlyList<string> relativePath)
    {
        if (TryResolve(relativePath) is not { } record)
        {
            return null;
        }

        if (tree.IsDirectory[record])
        {
            return SumSubtree(record);
        }

        return tree.SizeUnknown[record]
            ? null
            : new ScanSize(tree.Allocated[record], tree.Logical[record]);
    }

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
            if (tree.IsReparsePoint[current] || tree.Names[current] is not { } component)
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
    /// </summary>
    private uint? TryFindChild(uint directory, string name)
    {
        for (var i = links.Start[directory]; i < links.Start[directory + 1]; i++)
        {
            var child = links.Children[i];

            // Only directories carry names, so a file component never matches. Deguffer does measure
            // one file — C:\Windows\MEMORY.DMP — and this is why that measurement takes the walk:
            // naming every file on a volume would cost a string per record, which is a poor trade
            // for a single stat. See ScanStrategy.DirectRead, which is how the walk reports it.
            if (tree.Names[child] is { } candidate
                && candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
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
    /// </summary>
    private ScanSize? SumSubtree(uint root)
    {
        long allocated = 0;
        long logical = 0;

        var stack = new Stack<uint>();
        stack.Push(root);

        while (stack.TryPop(out var node))
        {
            // A link holds nothing and contributes nothing: whatever it points at keeps its own
            // place in the table, and the walk does not enter one either.
            if (tree.IsReparsePoint[node])
            {
                continue;
            }

            if (tree.SizeUnknown[node])
            {
                return null;
            }

            allocated += tree.Allocated[node];
            logical += tree.Logical[node];

            for (var i = links.Start[node]; i < links.Start[node + 1]; i++)
            {
                stack.Push(links.Children[i]);
            }
        }

        return new ScanSize(allocated, logical);
    }
}
