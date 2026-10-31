
namespace Deguffer.Core.Exploring;

/// <summary>
/// What a page that draws a <typeparamref name="TTree"/> says of one of the shapes on its map, which
/// the map cannot say for itself: what a node is, and what a block of the map stands for here.
///
/// <para>Asked only of a node the tree on screen holds, which <see cref="MapShapeFacts.For"/> and
/// <see cref="MapReadout.For"/> settle before they ask, so an answer never checks it again.</para>
/// </summary>
public interface IMapShapes<in TTree>
    where TTree : IShownTree
{
    /// <summary>What a screen reader is told of <paramref name="node"/>.</summary>
    MapShapeFacts FactsOf(TTree tree, int node);

    /// <summary>The line under the map, where the pointer or the keyboard is on <paramref name="node"/>.</summary>
    MapReadout ReadoutOf(TTree tree, int node);

    /// <summary>
    /// Why the space <paramref name="block"/> stands for is there, where the page can say; empty
    /// where it cannot.
    /// </summary>
    string NoteOn(TTree tree, MapBlock block);
}
