namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// What a layout reads from a tree to draw it: how big a node is, what it holds, whether it holds
/// anything, and in what order, with the parent and the root a drawing needs to colour a branch.
///
/// <para>A seam rather than the disk tree itself, because two trees are drawn by the same layouts:
/// <see cref="ExploreTree"/>, a scanned drive, and <see cref="Memory.MemoryTree"/>, where memory is.
/// Nothing a layout does depends on either being about disk or about memory, and nothing here says
/// which it is.</para>
/// </summary>
public interface ISizedTree
{
    /// <summary>The node every other node is under. It is its own parent.</summary>
    int RootNode { get; }

    /// <summary>
    /// The order <see cref="ChildrenOf"/> returns siblings in. A precondition rather than a detail:
    /// the treemap and the sunburst require <see cref="ExploreChildOrder.BySize"/>.
    /// </summary>
    ExploreChildOrder ChildOrder { get; }

    /// <summary>What the node accounts for, in bytes, its children included.</summary>
    long SizeOf(int node);

    /// <summary>The node's children, in <see cref="ChildOrder"/>.</summary>
    ReadOnlySpan<int> ChildrenOf(int node);

    /// <summary>
    /// Whether the node is the kind of thing that holds others, which is what a layout descends into.
    /// A folder is one even when empty, so this is not the same question as whether it has children.
    /// </summary>
    bool IsContainer(int node);

    /// <summary>The node above this one. The root answers with itself.</summary>
    int ParentOf(int node);
}
