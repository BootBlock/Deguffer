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
    /// </summary>
    public ScanSize? TryMeasure(IReadOnlyList<string> relativePath)
    {
        if (TryResolve(relativePath) is not { } record)
        {
            return null;
        }

        return tree.IsDirectory[record]
            ? SumSubtree(record)
            : new ScanSize(tree.Allocated[record], tree.Logical[record]);
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
            if (!tree.IsDirectory[current] || TryFindChild(current, component) is not { } next)
            {
                return null;
            }

            current = next;
        }

        return current;
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

            // Only directories carry names, so a file component never matches. That is correct for
            // the caller's purpose: every path Deguffer measures is a directory.
            if (tree.Names[child] is { } candidate
                && candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Iterative depth-first sum. Recursion would be the obvious shape and would overflow the stack
    /// on a deep node_modules tree, which is exactly the kind of tree this tool exists to measure.
    /// </summary>
    private ScanSize SumSubtree(uint root)
    {
        long allocated = 0;
        long logical = 0;

        var stack = new Stack<uint>();
        stack.Push(root);

        while (stack.TryPop(out var node))
        {
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
