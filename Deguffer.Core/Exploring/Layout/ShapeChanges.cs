using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// What became of the shapes between two layouts of the same place: which moved or changed size,
/// which appeared and which disappeared, so the map can move from the one to the other rather than
/// replace it (#282).
///
/// <para>Shapes are matched by what they are, not by number (<see cref="ITrackedTree"/>): a scan
/// builds a new tree for each snapshot, and a reading of memory renumbers every node.</para>
///
/// <para>Only the largest shapes are reported, and never one inside another. Each is shown on its
/// own as it moves, carrying the picture inside it, so a folder and a file in it reported together
/// would show that file twice. The shapes reported are a cut through the picture: the shapes drawn
/// at the top, each replaced by the shapes drawn inside it, largest first, for as long as that keeps
/// to <see cref="MostShapes"/>. Everything smaller moves with the shape it is in, and what is left
/// over (the lumps of shapes too small to draw one by one, a shape that did not move) crossfades
/// with the rest of the picture. In an icicle no shape is inside another, so the cut is the largest
/// shapes themselves.</para>
/// </summary>
public static class ShapeChanges
{
    /// <summary>
    /// How many shapes of each layout move on their own at most, whatever the drawing holds: the large
    /// shapes a reader follows move, and the work of each frame of the move is bounded by this rather
    /// than by the size of the tree, as every other shape crossfades.
    /// </summary>
    public const int MostShapes = 200;

    /// <summary>
    /// How far a shape has to move, or change size, in pixels of the later drawing, before it is said
    /// to have: anything less is the same rectangle, drawn the same in both, which crossfades unseen.
    /// </summary>
    private const double Still = 0.5;

    /// <summary>
    /// The shapes that moved between <paramref name="earlier"/> and <paramref name="later"/>, two
    /// drawings of the same place at the same zoom. Empty where their trees are not of the same thing,
    /// so there is nothing to match, or where either is not a drawing of rectangles, whose shapes are
    /// not moved.
    /// </summary>
    public static IReadOnlyList<ShapeChange> Between(ExploreSurface earlier, ExploreSurface later)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);

        return earlier is TiledSurface tiledEarlier && later is TiledSurface tiledLater ? Between(tiledEarlier, tiledLater) : [];
    }

    /// <summary>
    /// Work out now, on the caller's thread, the cut of each drawing that <see cref="Between"/> reads,
    /// where it would read one: each drawing's is made once and kept (<see cref="TiledSurface.Cut"/>).
    /// </summary>
    internal static void Prepare(ExploreSurface earlier, ExploreSurface later)
    {
        if (earlier is TiledSurface tiledEarlier
            && later is TiledSurface tiledLater
            && earlier.Tree is ITrackedTree before
            && before.CounterpartsIn(later.Tree) is not null)
        {
            _ = tiledEarlier.Cut;
            _ = tiledLater.Cut;
        }
    }

    private static IReadOnlyList<ShapeChange> Between(TiledSurface earlier, TiledSurface later)
    {
        if (earlier.Tree is not ITrackedTree before
            || later.Tree is not ITrackedTree after
            || before.CounterpartsIn(later.Tree) is not { } onward
            || after.CounterpartsIn(earlier.Tree) is not { } back)
        {
            return [];
        }

        var earlierCut = earlier.Cut;
        var laterCut = later.Cut;
        var stayed = new HashSet<int>();
        var changes = new List<ShapeChange>();

        foreach (var index in laterCut.Moving)
        {
            var tile = later.Tiles[index];
            var to = tile.FrameIn(later.Width, later.Height);

            if ((tile.IsNode ? back(tile.Node) : tile.Node) is { } was && earlierCut.IndexOf(was) is { } from)
            {
                stayed.Add(was);

                var moved = earlier.Tiles[from].FrameIn(earlier.Width, earlier.Height);

                if (!Same(moved, to, later))
                {
                    changes.Add(new ShapeChange(ShapeChangeKind.Moved, tile.Node, moved, to));
                }
            }
            else
            {
                changes.Add(new ShapeChange(ShapeChangeKind.Appeared, tile.Node, Point(to), to));
            }
        }

        // A shape of the earlier cut still drawn in the later layout moves with whatever it is in
        // there, or as the shapes it was cut into, and is already said. One no longer drawn went.
        foreach (var index in earlierCut.Moving)
        {
            var tile = earlier.Tiles[index];

            if (stayed.Contains(tile.Node)
                || ((tile.IsNode ? onward(tile.Node) : tile.Node) is { } now && laterCut.IndexOf(now) is not null))
            {
                continue;
            }

            var from = tile.FrameIn(earlier.Width, earlier.Height);

            changes.Add(new ShapeChange(ShapeChangeKind.Disappeared, tile.Node, from, Point(from)));
        }

        return changes;
    }

    private static MapFrame Point(MapFrame frame)
    {
        var (x, y) = frame.Centre;

        return new MapFrame(x, y, 0, 0);
    }

    private static bool Same(MapFrame a, MapFrame b, ExploreSurface drawing) =>
        Math.Abs(a.X - b.X) * drawing.Width < Still
        && Math.Abs(a.Y - b.Y) * drawing.Height < Still
        && Math.Abs(a.Width - b.Width) * drawing.Width < Still
        && Math.Abs(a.Height - b.Height) * drawing.Height < Still;
}
