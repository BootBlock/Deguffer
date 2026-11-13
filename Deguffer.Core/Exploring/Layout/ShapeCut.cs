using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One drawing of rectangles as a move between two layouts reads it (<see cref="ShapeChanges"/>): the
/// shapes that move on their own, out of the shapes a move can match.
///
/// <para>Fixed for the drawing, so made once for it (<see cref="TiledSurface.Cut"/>): a drawing is
/// read as the later of one move and again as the earlier of the next, and at 4K holds about a hundred
/// thousand rectangles.</para>
/// </summary>
internal sealed class ShapeCut
{
    /// <summary>The largest shape first, and of two the same size the one drawn first, so a cut is the same every time.</summary>
    private static readonly Comparer<(float Area, int Index)> LargestFirst =
        Comparer<(float Area, int Index)>.Create((x, y) => y.Area != x.Area ? y.Area.CompareTo(x.Area) : x.Index.CompareTo(y.Index));

    private readonly TiledSurface _drawing;

    /// <summary>
    /// The first shape of each block that stands for no node but is one of a kind, such as the volume's
    /// free space. A handful at most, and not in the drawing's index of nodes (<see cref="TiledSurface.FirstOf"/>).
    /// </summary>
    private readonly Dictionary<int, int> _blocks;

    private ShapeCut(TiledSurface drawing, Dictionary<int, int> blocks, int[] moving)
    {
        _drawing = drawing;
        _blocks = blocks;
        Moving = moving;
    }

    /// <summary>
    /// The indices of the shapes that move on their own: those drawn at the top, each replaced by what
    /// is drawn inside it, largest first, while there is room in <see cref="ShapeChanges.MostShapes"/>.
    /// In drawing order, or largest first where more shapes are drawn at the top than that. See
    /// <see cref="ShapeChanges"/>.
    /// </summary>
    public IReadOnlyList<int> Moving { get; }

    /// <summary>
    /// The index of the shape a move matches for <paramref name="thing"/>: a node's first shape, or the
    /// shape of a block that is one of a kind. Null for the lumps of shapes too small to draw, which
    /// stand for different things in different places and are never matched, and for anything not drawn.
    /// </summary>
    public int? IndexOf(int thing) =>
        thing >= 0 ? _drawing.FirstOf(thing) : _blocks.TryGetValue(thing, out var index) ? index : null;

    public static ShapeCut Of(TiledSurface drawing)
    {
        var tiles = drawing.Tiles;
        var blocks = new Dictionary<int, int>();
        var inside = new Dictionary<int, List<int>>();
        var top = new List<int>();

        // In drawing order, in which a folder comes before what it holds, so each shape's holder is
        // already indexed by the time the shape is met.
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];

            if (!tile.IsNode)
            {
                if (tile.Block != MapBlock.Aggregate && blocks.TryAdd(tile.Node, index))
                {
                    top.Add(index);
                }

                continue;
            }

            // Only a node's first shape is matched, so only it moves on its own.
            if (drawing.FirstOf(tile.Node) != index)
            {
                continue;
            }

            if (Holder(drawing, tile) is { } holder)
            {
                (inside.TryGetValue(holder, out var held) ? held : inside[holder] = []).Add(index);
            }
            else
            {
                top.Add(index);
            }
        }

        return new ShapeCut(drawing, blocks, Cut(tiles, top, inside));
    }

    private static int[] Cut(IReadOnlyList<ExploreTile> tiles, List<int> top, Dictionary<int, List<int>> inside)
    {
        if (top.Count > ShapeChanges.MostShapes)
        {
            top.Sort((x, y) => LargestFirst.Compare((Area(tiles[x]), x), (Area(tiles[y]), y)));

            return [.. top.GetRange(0, ShapeChanges.MostShapes)];
        }

        var open = new PriorityQueue<int, (float Area, int Index)>(LargestFirst);

        foreach (var index in top)
        {
            Offer(open, inside, tiles, index);
        }

        var members = new HashSet<int>(top);

        while (open.TryDequeue(out var index, out _))
        {
            var held = inside[index];

            if (members.Count - 1 + held.Count > ShapeChanges.MostShapes)
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

        var moving = new int[members.Count];

        members.CopyTo(moving);
        Array.Sort(moving);

        return moving;
    }

    private static void Offer(PriorityQueue<int, (float Area, int Index)> open, Dictionary<int, List<int>> inside, IReadOnlyList<ExploreTile> tiles, int index)
    {
        if (inside.ContainsKey(index))
        {
            open.Enqueue(index, (Area(tiles[index]), index));
        }
    }

    /// <summary>
    /// The shape of the nearest folder drawn above <paramref name="tile"/>, a node's shape, where the
    /// shape lies inside it, and null where it is drawn at the top or beside its folder, as in an icicle.
    /// The layouts draw every folder between a drawing's root and what it draws, so the walk stops at
    /// the parent for every shape but the root's.
    /// </summary>
    private static int? Holder(TiledSurface drawing, ExploreTile tile)
    {
        var tree = drawing.Tree;

        for (int node = tile.Node, parent = tree.ParentOf(node); parent != node; node = parent, parent = tree.ParentOf(node))
        {
            if (drawing.FirstOf(parent) is { } holder)
            {
                return TiledSurface.Inside(tile, drawing.Tiles[holder]) ? holder : null;
            }
        }

        return null;
    }

    private static float Area(ExploreTile tile) => tile.Width * tile.Height;
}
