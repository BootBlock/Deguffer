using Deguffer.Core.Exploring.Files;

namespace Deguffer.Core.Exploring.Links;

/// <summary>One broken link found in a scanned tree, by its node there.</summary>
public readonly record struct BrokenLinkNode(int Node, BrokenLink Link);

/// <summary>What <see cref="BrokenLinks.Find"/> found under one node of a tree.</summary>
/// <param name="Links">The links whose target is gone, by path, at most the limit asked for.</param>
/// <param name="Gone">How many links that pass the filter lead to a target that is gone, listed or not.</param>
/// <param name="Unreachable">
/// How many lead to a target Deguffer cannot reach. Counted so the list can say they were left out,
/// and never listed, because a link is offered only where its target is gone.
/// </param>
public sealed record BrokenLinkListing(IReadOnlyList<BrokenLinkNode> Links, int Gone, int Unreachable)
{
    /// <summary>Whether every link whose target is gone is listed.</summary>
    public bool IsComplete => Links.Count == Gone;
}

/// <summary>
/// The shortcuts, symbolic links and junctions at any depth under one node of a scanned tree whose
/// target is gone: what a removed program leaves in a folder of shortcuts, and what a removed folder
/// leaves wherever something linked to it.
///
/// <para>The tree says which entries are links and which files are named as shortcuts, so finding
/// them is a pass over the tree. Only each of those is then read, and its target asked about, a few
/// at a time (G4).</para>
///
/// <para><b>Ordered by path, never by anything about removal (§7.1).</b> Every link listed has a target
/// that is gone, which is a fact about the target, and the list says nothing about whether the link
/// may be removed. That is <c>ExploreActionPolicy</c>'s question, asked of each row as it is of every
/// other.</para>
/// </summary>
public static class BrokenLinks
{
    /// <summary>
    /// How many links are read at once. Each read is a load through the shell or a read of reparse
    /// data, and each target a question to the disk, so a few at once hides the wait of each without
    /// queueing the disk behind dozens.
    /// </summary>
    private const int ReadAtOnce = 4;

    /// <summary>See <see cref="LargestFiles"/>, whose stride this is for the same reason.</summary>
    private const int CancellationStride = 4096;

    /// <summary>
    /// The links at or below <paramref name="root"/> that pass <paramref name="filter"/> and whose
    /// target is gone, at most <paramref name="limit"/> of them, by path.
    ///
    /// <para>A link is never descended into: what it leads to keeps its own place in the tree, or is
    /// not in it. A shortcut is a file named <c>.lnk</c>, as Explorer decides it.</para>
    /// </summary>
    /// <param name="now">The instant an age criterion is measured back from.</param>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled.</exception>
    public static BrokenLinkListing Find(
        ExploreTree tree,
        int root,
        FileFilter filter,
        DateTime now,
        LinkInspection inspection,
        int limit = LargestFiles.Limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var candidates = Candidates(tree, root, filter, FileAges.Cutoff(filter.UnwrittenFor, now), ct);
        var found = new BrokenLink?[candidates.Count];

        Parallel.For(
            0,
            candidates.Count,
            new ParallelOptions { MaxDegreeOfParallelism = ReadAtOnce, CancellationToken = ct },
            at =>
            {
                var node = candidates[at];
                found[at] = inspection.Inspect(tree.PathOf(node), tree.IsLink(node), tree.IsDirectory(node), tree.SizeOf(node));
            });

        List<BrokenLinkNode> gone = [];
        var unreachable = 0;

        for (var at = 0; at < found.Length; at++)
        {
            switch (found[at])
            {
                case { IsOffered: true } link:
                    gone.Add(new BrokenLinkNode(candidates[at], link));
                    break;

                case not null:
                    unreachable++;
                    break;
            }
        }

        gone.Sort(static (one, other) => StringComparer.OrdinalIgnoreCase.Compare(one.Link.Path, other.Link.Path));

        return new BrokenLinkListing(gone.Count > limit ? gone.GetRange(0, limit) : gone, gone.Count, unreachable);
    }

    /// <summary>Every link and every shortcut at or below <paramref name="root"/> that passes the filter.</summary>
    private static List<int> Candidates(ExploreTree tree, int root, FileFilter filter, DateTime? cutoff, CancellationToken ct)
    {
        List<int> candidates = [];
        var waiting = new Stack<int>();
        var visited = 0;

        waiting.Push(root);

        // Iterative for the reason LargestFiles is: a deep tree overflows a recursive pass.
        while (waiting.TryPop(out var node))
        {
            if (++visited % CancellationStride == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var isLink = tree.IsLink(node);

            if (!isLink && tree.IsDirectory(node))
            {
                foreach (var child in tree.ChildrenOf(node))
                {
                    waiting.Push(child);
                }

                continue;
            }

            if ((isLink || WindowsLinkTargetReader.IsShortcutName(tree.NameSpanOf(node))) && filter.Matches(tree, node, cutoff))
            {
                candidates.Add(node);
            }
        }

        return candidates;
    }
}
