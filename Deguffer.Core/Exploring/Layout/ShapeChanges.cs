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
    /// How many shapes of each layout move on their own at most. Enough that the large shapes a
    /// reader follows move, and few enough that each frame of the move stays within its budget on a
    /// tree of a million files, where every other shape crossfades.
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
    /// so there is nothing to match.
    /// </summary>
    public static IReadOnlyList<ShapeChange> Between(ExploreSurface earlier, ExploreSurface later)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);

        if (earlier.Tree is not ITrackedTree before
            || later.Tree is not ITrackedTree after
            || before.CounterpartsIn(later.Tree) is not { } onward
            || after.CounterpartsIn(earlier.Tree) is not { } back)
        {
            return [];
        }

        var earlierTiles = Indexed(earlier.Tiles);
        var laterTiles = Indexed(later.Tiles);
        var stayed = new HashSet<int>();
        var changes = new List<ShapeChange>();

        foreach (var index in Cut(later, laterTiles))
        {
            var tile = later.Tiles[index];
            var to = FrameOf(tile, later);

            if ((tile.IsNode ? back(tile.Node) : tile.Node) is { } was && earlierTiles.TryGetValue(was, out var from))
            {
                stayed.Add(was);

                var moved = FrameOf(earlier.Tiles[from], earlier);

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
        foreach (var index in Cut(earlier, earlierTiles))
        {
            var tile = earlier.Tiles[index];

            if (stayed.Contains(tile.Node)
                || ((tile.IsNode ? onward(tile.Node) : tile.Node) is { } now && laterTiles.ContainsKey(now)))
            {
                continue;
            }

            var from = FrameOf(tile, earlier);

            changes.Add(new ShapeChange(ShapeChangeKind.Disappeared, tile.Node, from, Point(from)));
        }

        return changes;
    }

    /// <summary>
    /// Where each node's shape is in <paramref name="tiles"/>, and each shape that stands for no node
    /// but is one of a kind, such as the volume's free space. Not the lumps of shapes too small to
    /// draw, which stand for different things in different places and are never matched.
    /// </summary>
    private static Dictionary<int, int> Indexed(IReadOnlyList<ExploreTile> tiles)
    {
        var indexed = new Dictionary<int, int>(tiles.Count);

        for (var index = 0; index < tiles.Count; index++)
        {
            if (!tiles[index].IsAggregate)
            {
                indexed.TryAdd(tiles[index].Node, index);
            }
        }

        return indexed;
    }

    /// <summary>
    /// The shapes of <paramref name="drawing"/> that move on their own: those drawn at the top, each
    /// replaced by what is drawn inside it, largest first, while there is room in
    /// <see cref="MostShapes"/>. See <see cref="ShapeChanges"/>.
    /// </summary>
    private static List<int> Cut(ExploreSurface drawing, Dictionary<int, int> indexed)
    {
        var tiles = drawing.Tiles;
        var inside = new Dictionary<int, List<int>>();
        var cut = new List<int>();

        foreach (var index in indexed.Values)
        {
            if (Holder(drawing, indexed, index) is { } holder)
            {
                (inside.TryGetValue(holder, out var held) ? held : inside[holder] = []).Add(index);
            }
            else
            {
                cut.Add(index);
            }
        }

        if (cut.Count > MostShapes)
        {
            return [.. cut.OrderByDescending(index => Area(tiles[index])).ThenBy(index => index).Take(MostShapes)];
        }

        var open = new PriorityQueue<int, (float Area, int Index)>(
            Comparer<(float Area, int Index)>.Create((x, y) => y.Area != x.Area ? y.Area.CompareTo(x.Area) : x.Index.CompareTo(y.Index)));

        foreach (var index in cut)
        {
            Offer(open, inside, tiles, index);
        }

        var members = new HashSet<int>(cut);

        while (open.TryDequeue(out var index, out _))
        {
            var held = inside[index];

            if (members.Count - 1 + held.Count > MostShapes)
            {
                continue;
            }

            members.Remove(index);

            foreach (var child in held)
            {
                members.Add(child);
                Offer(open, inside, tiles, child);
            }
        }

        return [.. members.Order()];
    }

    private static void Offer(PriorityQueue<int, (float Area, int Index)> open, Dictionary<int, List<int>> inside, IReadOnlyList<ExploreTile> tiles, int index)
    {
        if (inside.ContainsKey(index))
        {
            open.Enqueue(index, (Area(tiles[index]), index));
        }
    }

    /// <summary>
    /// The shape of the nearest folder drawn above the shape at <paramref name="index"/>, where the
    /// shape lies inside it, and null where it is drawn at the top or beside its folder, as in an
    /// icicle. A shape that stands for no node is drawn at the top.
    /// </summary>
    private static int? Holder(ExploreSurface drawing, Dictionary<int, int> indexed, int index)
    {
        var tile = drawing.Tiles[index];

        if (!tile.IsNode)
        {
            return null;
        }

        var tree = drawing.Tree;

        for (int node = tile.Node, parent = tree.ParentOf(node); parent != node; node = parent, parent = tree.ParentOf(node))
        {
            if (indexed.TryGetValue(parent, out var holder))
            {
                return TiledSurface.Inside(tile, drawing.Tiles[holder]) ? holder : null;
            }
        }

        return null;
    }

    private static MapFrame FrameOf(ExploreTile tile, ExploreSurface drawing) =>
        new(tile.X / (double)drawing.Width, tile.Y / (double)drawing.Height, tile.Width / (double)drawing.Width, tile.Height / (double)drawing.Height);

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

    private static float Area(ExploreTile tile) => tile.Width * tile.Height;
}
