using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring;

/// <summary>
/// A tree a page shows and names the nodes of: a scanned drive, or where memory is. Unlike a step of a
/// replay, which is only drawn, a page answers questions about such a tree's nodes, so it has to know
/// which nodes the tree still holds.
/// </summary>
public interface IShownTree : ISizedTree
{
    /// <summary>
    /// Whether this tree holds <paramref name="node"/>, so a page may name it. A number a shape still
    /// carries can be one the tree has since let go of, such as a node removed from a scanned drive.
    /// </summary>
    bool Holds(int node);
}
