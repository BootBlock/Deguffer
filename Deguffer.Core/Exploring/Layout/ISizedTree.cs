namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// What the layouts and the drawings read from a tree: how big a node is, what it holds, whether a
/// layout should look inside it, and in what order, with the parent and the root a drawing needs to
/// colour a branch.
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
    /// Whether a layout should look inside this node.
    ///
    /// <para>Each tree answers in its own terms. A scanned drive answers by kind, so an empty folder is
    /// still a container: it holds nothing today and is not a file. A memory tree answers by whether
    /// anything is under it, because a process that started nothing holds only its own memory and has
    /// nothing to look inside.</para>
    /// </summary>
    bool IsContainer(int node);

    /// <summary>The node above this one. The root answers with itself.</summary>
    int ParentOf(int node);
}
