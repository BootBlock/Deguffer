using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// The map changing from one view of a place to another, from the treemap to the icicle or the
/// sunburst and back (#283): the largest shapes travel from their rectangle to their bar or wedge, so
/// the reader sees that the two pictures are of one tree, and the rest of the picture crossfades.
///
/// <para>The travelling shapes are decoration. They are flat shapes in the colour the new view paints
/// them in, and they go once the move is over, leaving the new view's own drawing, which is the only
/// record of what is where. Nothing on the map can be pointed at or clicked while they move, because
/// no shape is where either view puts it (§7.1).</para>
/// </summary>
public static class ViewMorph
{
    /// <summary>
    /// How many shapes travel at most. Every frame of the move blends each one's outline, of
    /// <see cref="MorphOutline.Points"/> points, so this bounds the work of a frame whatever the tree
    /// holds, and the largest shapes, the ones a reader follows, are the ones that travel.
    /// </summary>
    public const int MostShapes = 200;

    /// <summary>
    /// Whether a change from <paramref name="from"/> to <paramref name="to"/> on a map of
    /// <paramref name="tree"/> is a change between two of the views that draw a picture of it, the
    /// treemap, the icicle and the sunburst, as each is actually drawn
    /// (<see cref="ExploreSurface.Drawn"/>). A scan still running is drawn as an icicle whichever
    /// view is picked, so a change of view during one changes no picture, and has nothing to morph.
    /// </summary>
    public static bool Morphs(ISizedTree tree, ExploreView from, ExploreView to)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var before = ExploreSurface.Drawn(tree, from);
        var after = ExploreSurface.Drawn(tree, to);

        return before != after && Pictures(before) && Pictures(after);
    }

    /// <summary>
    /// The shapes that travel from <paramref name="earlier"/> to <paramref name="later"/>, two views of
    /// one tree on one canvas: the <see cref="MostShapes"/> largest nodes both draw, ordered as they
    /// are painted, so a shape the treemap draws inside another is painted over it. Empty where the
    /// two are of different trees or on different canvases, which have no shape in common to travel.
    /// Empty where either is a zoomed part of its picture: a view changes from the whole of one
    /// picture to the whole of the other, and only the treemap zooms.
    /// </summary>
    public static IReadOnlyList<ShapeMorph> Between(ExploreSurface earlier, ExploreSurface later)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);

        if (!ReferenceEquals(earlier.Tree, later.Tree)
            || earlier.Width != later.Width
            || earlier.Height != later.Height
            || !IsWhole(earlier)
            || !IsWhole(later))
        {
            return [];
        }

        var tree = later.Tree;
        var drawnBefore = earlier.NodesDrawn.ToHashSet();
        var largest = later.NodesDrawn
            .Where(drawnBefore.Contains)
            .OrderByDescending(tree.SizeOf)
            .ThenBy(node => node)
            .Take(MostShapes)
            .ToHashSet();

        if (largest.Count == 0)
        {
            return [];
        }

        var from = earlier.MorphShapes(largest).ToDictionary(shape => shape.Node);

        return
        [
            .. later.MorphShapes(largest)
                .OrderBy(shape => shape.Depth)
                .ThenBy(shape => shape.Node)
                .Select(to => new ShapeMorph(to.Node, to.Colour, from[to.Node].Outline, to.Outline, to.Edges)),
        ];
    }

    private static bool Pictures(ExploreView view) => view is ExploreView.Treemap or ExploreView.Icicle or ExploreView.Sunburst;

    private static bool IsWhole(ExploreSurface drawing) => drawing.Viewport is not { IsWhole: false };
}
