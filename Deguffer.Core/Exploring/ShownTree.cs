using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring;

/// <summary>
/// Which tree a page may answer a map's question about one of its shapes from: only the tree on
/// screen, and only for a node it still holds (§7.1).
///
/// <para>A map asks about the tree it drew, which is not always the one the page shows: a step of a
/// replay of what grew is drawn from a tree of its own, whose node numbers mean nothing in the page's,
/// and a shape can outlive the removal of its node by a frame. Answered from the page's tree, either
/// would name something else.</para>
/// </summary>
public static class ShownTree
{
    /// <summary>
    /// <paramref name="shown"/>, where <paramref name="asked"/> is that very tree and holds
    /// <paramref name="node"/>; otherwise null.
    /// </summary>
    public static TTree? Of<TTree>(ISizedTree asked, TTree? shown, int node)
        where TTree : class, IShownTree =>
        shown is not null && ReferenceEquals(asked, shown) && shown.Holds(node) ? shown : null;
}
