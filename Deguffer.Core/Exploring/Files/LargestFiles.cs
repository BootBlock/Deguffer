namespace Deguffer.Core.Exploring.Files;

/// <summary>
/// The largest files at any depth under one node of a scanned tree: an ISO in a forgotten folder, a
/// virtual machine's disk, an installer from three years ago — the things a map hides several levels
/// down.
///
/// <para>One pass over the subtree into a bounded heap, so finding the top thousand of a volume of
/// millions holds a thousand nodes and never sorts the rest (G4). The pass counts every file that
/// passes the filter as it goes, which is what lets the list say how many it is the largest of.</para>
///
/// <para><b>Ordered by size and by nothing else (§7.1).</b> No file is ranked by how removable it is,
/// and nothing here knows what may be removed. That is <c>ExploreActionPolicy</c>'s question, asked
/// of each row by the page.</para>
/// </summary>
public static class LargestFiles
{
    /// <summary>
    /// How many files the Files layout lists. A thousand is what WizTree lists, and past it the
    /// files are small enough that the map answers better than a list does.
    /// </summary>
    public const int Limit = 1000;

    /// <summary>
    /// How many nodes are visited between two looks at the cancellation token. A pass over a whole
    /// volume is millions of nodes, and a look per node would cost more than the filter.
    /// </summary>
    private const int CancellationStride = 4096;

    /// <summary>
    /// Which of two files the heap gives up first: the smaller, and of two the same size, the later
    /// node. The heap's head is therefore the one the next larger file replaces.
    /// </summary>
    private static readonly Comparer<(long Size, int Node)> Weakest = Comparer<(long Size, int Node)>.Create(
        (left, right) => left.Size != right.Size ? left.Size.CompareTo(right.Size) : right.Node.CompareTo(left.Node));

    /// <summary>
    /// The <paramref name="limit"/> largest files at or below <paramref name="root"/> that pass
    /// <paramref name="filter"/>, largest first.
    ///
    /// <para>Never a directory and never a link. A directory is what holds the files this lists, and
    /// a link holds nothing in this tree: its target keeps its own place, so listing it would count
    /// one file's bytes twice.</para>
    ///
    /// <para><paramref name="previous"/> is reused where it already holds the answer, which is the
    /// common case of a filter being typed into one keystroke at a time:</para>
    /// <list type="bullet">
    /// <item>The same question gets the same answer back.</item>
    /// <item>A narrower filter is applied to an earlier result that listed everything it matched.</item>
    /// <item>A higher minimum is applied even to an earlier result that was cut at the limit, provided
    /// the new minimum drops at least one file from it. Every file the cut left out was no larger than
    /// the smallest file it kept, and that one is now below the minimum, so nothing left out can
    /// return.</item>
    /// </list>
    /// <para>Anything else is a fresh pass.</para>
    /// </summary>
    /// <param name="now">The instant an age criterion is measured back from.</param>
    /// <param name="previous">The last answer for the same page, or null.</param>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled during the pass.</exception>
    public static FileRanking Find(
        ExploreTree tree,
        int root,
        FileFilter filter,
        DateTime now,
        int limit = Limit,
        FileRanking? previous = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var cutoff = FileAges.Cutoff(filter.UnwrittenFor, now);

        if (Reused(tree, root, filter, cutoff, limit, previous) is { } reused)
        {
            return reused;
        }

        var heap = new PriorityQueue<int, (long Size, int Node)>(Math.Min(limit, tree.NodeCount) + 1, Weakest);
        var waiting = new Stack<int>();
        var matched = 0;
        var visited = 0;

        waiting.Push(root);

        // Iterative for the reason ExploreTree's own passes are: a deep node_modules tree overflows
        // the stack of a recursive one, and it is exactly the kind of tree a disk is full of.
        while (waiting.TryPop(out var node))
        {
            if (++visited % CancellationStride == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (tree.IsLink(node))
            {
                continue;
            }

            if (tree.IsDirectory(node))
            {
                foreach (var child in tree.ChildrenOf(node))
                {
                    waiting.Push(child);
                }

                continue;
            }

            if (!filter.Matches(tree, node, cutoff))
            {
                continue;
            }

            matched++;

            var rank = (tree.SizeOf(node), node);

            if (heap.Count < limit)
            {
                heap.Enqueue(node, rank);
            }
            else if (heap.TryPeek(out _, out var weakest) && Weakest.Compare(rank, weakest) > 0)
            {
                heap.EnqueueDequeue(node, rank);
            }
        }

        var files = new int[heap.Count];

        // The heap gives up its weakest first, so filling from the end leaves the largest first.
        for (var at = files.Length - 1; at >= 0; at--)
        {
            files[at] = heap.Dequeue();
        }

        return new FileRanking(tree, root, filter, cutoff, limit, files, matched);
    }

    /// <summary>
    /// The answer <paramref name="previous"/> already holds for this question, or null where it does
    /// not hold one. See <see cref="Find"/> for the three cases.
    /// </summary>
    private static FileRanking? Reused(
        ExploreTree tree, int root, FileFilter filter, DateTime? cutoff, int limit, FileRanking? previous)
    {
        if (previous is null || !ReferenceEquals(previous.Tree, tree) || previous.Root != root || previous.Limit != limit)
        {
            return null;
        }

        if (filter == previous.Filter && cutoff == previous.Cutoff)
        {
            return previous;
        }

        if (!filter.Narrows(previous.Filter, cutoff, previous.Cutoff))
        {
            return null;
        }

        if (!previous.IsComplete && !filter.OnlyRaisesMinimumOf(previous.Filter, cutoff, previous.Cutoff))
        {
            return null;
        }

        int[] kept = [.. previous.Files.Where(node => filter.Matches(tree, node, cutoff))];

        // A cut result whose every file clears the new minimum says nothing about the files the cut
        // left out, which may clear it too.
        return previous.IsComplete || kept.Length < previous.Files.Count
            ? new FileRanking(tree, root, filter, cutoff, limit, kept, kept.Length)
            : null;
    }
}
