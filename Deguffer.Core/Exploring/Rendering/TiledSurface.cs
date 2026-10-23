using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The drawing behind both rectangular views. A treemap and an icicle differ in where the
/// rectangles go and in nothing after that, so they are one surface handed two layouts rather than
/// two surfaces repeating the same painting, pointing and labelling.
/// </summary>
public sealed class TiledSurface : ExploreSurface
{
    /// <summary>
    /// How far a shape may pass another's edge and still be inside it: the rounding of single
    /// precision, because a folder drawn with no room round what it holds ends where its last shape
    /// does, by a sum that need not round the same way twice.
    /// </summary>
    private const float Rounding = 1f / 64;

    private readonly IReadOnlyList<ExploreTile> _tiles;
    private readonly TileHitTest _hits;

    /// <summary>The volume a treemap of the whole of it draws beside its root, which each of its tiles lays out too.</summary>
    private readonly VolumeSpace _beside;

    /// <param name="viewport">
    /// The part of the picture <paramref name="tiles"/> were laid out for, or null where their layout
    /// draws only the whole of it. See <see cref="ExploreSurface.Viewport"/>.
    /// </param>
    /// <param name="beside">
    /// The volume the layout was given to draw beside the root, or <see cref="VolumeSpace.None"/>.
    /// See <see cref="ExploreSurface.HasVolumeBeside"/>.
    /// </param>
    public TiledSurface(
        ISizedTree tree,
        int root,
        int width,
        int height,
        LayoutLimits limits,
        ShapeColours colours,
        MapStyle style,
        IReadOnlyList<ExploreTile> tiles,
        MapViewport? viewport = null,
        VolumeSpace beside = default)
        : base(tree, root, width, height, limits, colours, style, viewport)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        _tiles = tiles;
        _hits = new TileHitTest(tiles, width, height);
        _beside = beside;
        HasVolumeBeside = TreemapLayout.DrawsBeside(tree.SizeOf(root), beside, width, height, limits);

        Labels = new TileLabels(
            tiles, tree, root, width, height, limits, style.ShapeNames, MaximumLabels,
            tile => TextColourFor(tile.Node, tile.Depth, tile.Framed)).Build();
        Ceiling = viewport is { } drawn ? MapCeiling.Of(drawn.Zoom, Deeper()) : null;
    }

    public override IReadOnlyList<ExploreLabel> Labels { get; }

    public override bool HasVolumeBeside { get; }

    public override double? Ceiling { get; }

    public override IReadOnlyList<ExploreTile> Tiles => _tiles;

    public override CanvasPainter Painter(TileColour background) => new TileRasteriser(Quads(), background);

    public override CushionQuads Quads() => new(_tiles, Width, Height, ColourOf, Finish);

    public override ExploreHit? At(float x, float y) =>
        _hits.At(x, y) is { } index ? new ExploreHit(_tiles[index].Node, _tiles[index].Bytes) : null;

    public override ExploreTile? TileAt(float x, float y) => _hits.At(x, y) is { } index ? _tiles[index] : null;

    /// <summary>
    /// Lay out <paramref name="key"/> of this treemap's picture, cut into tiles by
    /// <paramref name="tiling"/>: the same tree, folder, limits and volume beside it, coloured as this
    /// drawing is, at the tile's level of detail. Only for a treemap that can be zoomed, and a tiling of
    /// this drawing's canvas.
    /// </summary>
    public MapTile Tile(MapTiling tiling, MapTileKey key)
    {
        ArgumentNullException.ThrowIfNull(tiling);

        if (Viewport is not { } drawnAt)
        {
            throw new InvalidOperationException("Only a treemap that can be zoomed is cut into tiles.");
        }

        if (tiling.Width != Width || tiling.Height != Height)
        {
            throw new ArgumentException(
                $"A {tiling.Width}x{tiling.Height} tiling is not of this {Width}x{Height} drawing's picture.",
                nameof(tiling));
        }

        var zoom = MapTiling.ZoomOf(key.Level);
        var (width, height) = tiling.PixelsOf(key);
        var viewport = tiling.ViewportOf(key);
        var shapes = TreemapLayout.Compute(
            Tree, Root, Width, Height, Limits, _beside, viewport,
            new TreemapDetail(Limits, zoom, MapTiling.DetailOf(key.Level)), width, height);

        return new MapTile(this, drawnAt, key, viewport, tiling.FrameOf(key), width, height, shapes, ColourOf, Finish);
    }

    public override MapFrame? FrameOf(int node) => Deepest(node)?.FrameIn(Width, Height);

    public override int? ShownAs(int node) => Deepest(node)?.Node;

    /// <summary>
    /// Laid out again for a treemap that can be zoomed, whose drawing holds only what reaches the
    /// canvas. Any other drawing of rectangles holds all of its picture already.
    /// </summary>
    public override MapFrame? Locate(int node) =>
        Viewport is { } viewport
            ? TreemapLayout.Locate(Tree, Root, Width, Height, Limits, _beside, viewport, node)
            : Deepest(node) is { } shape && shape.Node == node ? shape.FrameIn(Width, Height) : null;

    protected override IReadOnlyList<MapShape> FindShapes(int? folder) =>
        Viewport is { } viewport
            ? TreemapLayout.Within(Tree, Root, Width, Height, Limits, _beside, viewport, folder is null && HasVolumeBeside ? null : folder ?? Root)
            : Held(folder ?? Root);

    public override IReadOnlyList<ExploreOutline> OutlineOf(MapShape shape) =>
    [
        OutlineOf(new ExploreTile(
            shape.Node,
            Depth: 0,
            shape.Bytes,
            (float)(shape.Frame.X * Width),
            (float)(shape.Frame.Y * Height),
            (float)(shape.Frame.Width * Width),
            (float)(shape.Frame.Height * Height))),
    ];

    /// <summary>
    /// The shapes in the row under <paramref name="folder"/>'s that it holds: an icicle draws what a
    /// folder holds below it rather than in it. Its block of the rest is the one in that row under the
    /// folder's own width.
    /// </summary>
    private List<MapShape> Held(int folder)
    {
        var shapes = new List<MapShape>();
        var holder = -1;

        for (var i = 0; i < _tiles.Count && holder < 0; i++)
        {
            holder = _tiles[i].IsNode && _tiles[i].Node == folder ? i : -1;
        }

        if (holder < 0)
        {
            return shapes;
        }

        var outer = _tiles[holder];

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            if (tile.Depth == outer.Depth + 1
                && (tile.IsNode ? Tree.ParentOf(tile.Node) == folder : Across(tile, outer)))
            {
                shapes.Add(MapShape.Of(tile.Node, tile.Bytes, tile.FrameIn(Width, Height)));
            }
        }

        return shapes;
    }

    /// <summary>
    /// A shape is under another where it lies inside it, which in a treemap is every shape with a
    /// folder drawn round it and in an icicle none, whose children sit beside their folder rather than
    /// in it. Read from where the shapes are rather than from which layout made them, so the answer is
    /// what the screen shows. Only a node's own folder is asked: the layouts draw nothing inside a
    /// shape but what that shape holds.
    /// </summary>
    public override IReadOnlyList<ExploreOutline> Uncovered(Func<int, bool> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        // The layouts emit a folder before what it holds, so its shape is known by the time they are.
        var shapes = new Dictionary<int, int>();
        var covered = new HashSet<int>();

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            if (!tile.IsNode)
            {
                continue;
            }

            shapes[tile.Node] = i;

            if (tile.Node != Root && shapes.TryGetValue(Tree.ParentOf(tile.Node), out var folder) && Inside(tile, _tiles[folder]))
            {
                covered.Add(_tiles[folder].Node);
            }
        }

        var outlines = new List<ExploreOutline>();

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            if (tile.IsNode && !covered.Contains(tile.Node) && nodes(tile.Node))
            {
                outlines.Add(OutlineOf(tile));
            }
        }

        return outlines;
    }

    /// <summary>
    /// One pass over the shapes, keeping the deepest that is the node or a folder above it. A node
    /// outside this drawing has none of them drawn here: every shape is this drawing's root or under it.
    /// </summary>
    private ExploreTile? Deepest(int node)
    {
        var path = PathOf(node);
        ExploreTile? deepest = null;

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            if (tile.IsNode && path.Contains(tile.Node) && (deepest is not { } found || tile.Depth > found.Depth))
            {
                deepest = tile;
            }
        }

        return deepest;
    }

    public override IReadOnlyList<ExploreOutline> Outlines(IReadOnlySet<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var outlines = new List<ExploreOutline>();

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            if (tile.IsNode && nodes.Contains(tile.Node))
            {
                outlines.Add(OutlineOf(tile));
            }
        }

        return outlines;
    }

    public override IEnumerable<int> NodesDrawn => _tiles.Where(tile => tile.IsNode).Select(tile => tile.Node).Distinct();

    public override IReadOnlyList<MorphShape> MorphShapes(IReadOnlySet<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var shapes = new List<MorphShape>();
        var found = new HashSet<int>();

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            if (tile.IsNode && nodes.Contains(tile.Node) && found.Add(tile.Node))
            {
                var colour = ColourOf(tile);

                shapes.Add(new MorphShape(tile.Node, tile.Depth, colour, MorphOutline.Of(tile), Finish.EdgesOf(colour, tile.Framed)));
            }
        }

        return shapes;
    }

    private static ExploreOutline OutlineOf(ExploreTile tile)
    {
        var right = tile.X + tile.Width;
        var bottom = tile.Y + tile.Height;

        return new ExploreOutline(tile.Node, [
            new ExplorePoint(tile.X, tile.Y),
            new ExplorePoint(right, tile.Y),
            new ExplorePoint(right, bottom),
            new ExplorePoint(tile.X, bottom),
        ]);
    }

    /// <summary>Whether <paramref name="inner"/> lies inside <paramref name="outer"/>, to within <see cref="Rounding"/>.</summary>
    internal static bool Inside(ExploreTile inner, ExploreTile outer) =>
        Across(inner, outer)
            && inner.Y >= outer.Y - Rounding
            && inner.Y + inner.Height <= outer.Y + outer.Height + Rounding;

    /// <summary>
    /// Whether <paramref name="inner"/> lies within <paramref name="outer"/>'s width, to within
    /// <see cref="Rounding"/>: all an icicle's row below a folder needs, which sits under it rather
    /// than inside it.
    /// </summary>
    private static bool Across(ExploreTile inner, ExploreTile outer) =>
        inner.X >= outer.X - Rounding && inner.X + inner.Width <= outer.X + outer.Width + Rounding;

    /// <summary>
    /// How many times more the picture has to be magnified for everything in view to be drawn one by
    /// one, and for the smallest shape drawn to have room for its name: 1 where both already hold.
    /// Every tile is in view, in part at least: a zoomed layout lays out nothing wholly off the canvas.
    /// </summary>
    private double Deeper()
    {
        var deeper = 1.0;

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            if (tile.Finest > 0)
            {
                deeper = Math.Max(deeper, MapCeiling.Revealing(tile.Finest, Limits.MinimumTileSize));
            }
            else if (tile.IsNode)
            {
                deeper = Math.Max(
                    deeper,
                    MapCeiling.Naming(tile.Width, tile.Height, Limits.MinimumLabelWidth, Limits.MinimumLabelHeight));
            }
        }

        return deeper;
    }
}
