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
    /// How many folders to name in their frames at most, on top of the labels inside the shapes.
    ///
    /// <para>Separate from <see cref="ExploreSurface.MaximumLabels"/>, which keeps the text over the
    /// picture from becoming noise. A folder's name is in a band the layout set aside for it, so it
    /// covers nothing, and a band left empty reads as a folder with no name — so this is set high
    /// enough that a real drive at 4K does not reach it. A band needs a folder at least three lines
    /// of text tall and a name wide, which a 3840 by 2160 canvas fits under two thousand of side by
    /// side; nesting and the mix of sizes on a real drive leave far fewer. This bounds the controls
    /// the shell creates for a canvas that somehow has more, and the smallest folders are the ones
    /// that go without.</para>
    /// </summary>
    private const int MaximumHeaders = 1024;

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

        Labels = BuildLabels();
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
    /// Lay the labels over the finished bitmap: each framed folder's name in the band along its top,
    /// and a name inside each shape with nothing drawn in it.
    ///
    /// <para>A shape that has children drawn inside it and no band gets no label. Its children are
    /// inset by a pixel or two, so its label and its first child's would land within a few pixels of
    /// each other and overprint into an unreadable stack. The band is what makes a folder's own name
    /// affordable, because nothing is drawn in it.</para>
    ///
    /// <para>The largest shapes are named first, in both kinds. The layout emits shapes depth first,
    /// so taking them in that order would spend the whole allowance on the first branch and leave
    /// the largest folder elsewhere unnamed.</para>
    ///
    /// <para>Every one of those decisions is made on the part of a shape that is on the canvas. A
    /// zoomed treemap's shapes run off its edges, and a name placed at a shape's own left edge would
    /// be off the canvas with it, or a shape judged by its whole size would be named in a sliver too
    /// small to read. So a folder whose band runs off the left is named where the band comes on, and
    /// a band that is off the canvas altogether is not named.</para>
    /// </summary>
    private IReadOnlyList<ExploreLabel> BuildLabels()
    {
        var (headers, insides) = Candidates();
        var labels = new List<ExploreLabel>(Math.Min(headers.Count, MaximumHeaders) + Math.Min(insides.Count, MaximumLabels));

        Largest(headers, MaximumHeaders);
        Largest(insides, MaximumLabels);

        for (var i = 0; i < headers.Count; i++)
        {
            labels.Add(HeaderLabel(headers[i]));
        }

        for (var i = 0; i < insides.Count; i++)
        {
            labels.Add(InsideLabel(insides[i]));
        }

        return labels;
    }

    /// <summary>
    /// A shape to be named, with the part of it on the canvas, which every decision about its name is
    /// made on: worked out once, rather than again for each comparison of the sort and for the label.
    /// </summary>
    private readonly record struct Named(ExploreTile Tile, ExploreTile Shown)
    {
        public float Area => Shown.Width * Shown.Height;
    }

    /// <summary>Largest shape on the canvas first, for <see cref="Largest"/>.</summary>
    private static readonly Comparison<Named> LargestFirst = (a, b) => b.Area.CompareTo(a.Area);

    /// <summary>
    /// The folders whose band has room for their name, and the shapes with room for a name inside
    /// them and nothing drawn in them, in the order the layout emitted them.
    ///
    /// <para>Indexed rather than foreached. A treemap of a real volume is tens of thousands of
    /// rectangles, and enumerating an IReadOnlyList boxes the list's own struct enumerator and
    /// dispatches every step through the interface: the same cost TileHitTest dropped its iterator
    /// to avoid, on the same list and once per repaint (G5).</para>
    /// </summary>
    private (List<Named> Headers, List<Named> Insides) Candidates()
    {
        var covered = Covered();
        var headers = new List<Named>();
        var insides = new List<Named>();

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var shown = OnCanvas(tile);

            if (tile.Header > 0)
            {
                // A name wide, as the layout asked of the whole band before it gave one. A band cut
                // to a sliver by the canvas's edge is not named, and a name narrower than its own
                // padding would come out with a negative width.
                if (tile.Y + tile.Header > 0 && tile.Y < Height && shown.Width >= Limits.MinimumLabelWidth)
                {
                    headers.Add(new Named(tile, shown));
                }
            }
            else if (Style.ShapeNames
                && !tile.IsAggregate
                && (tile.IsFreeSpace || (tile.Node != Root && !covered.Contains(tile.Node)))
                && shown.HasRoomForALabel(Limits))
            {
                insides.Add(new Named(tile, shown));
            }
        }

        return (headers, insides);
    }

    /// <summary>
    /// Which nodes had a rectangle drawn inside them. The layouts emit a parent before its children,
    /// so this is complete for every tile by the time <see cref="Candidates"/> reaches it.
    /// </summary>
    private HashSet<int> Covered()
    {
        var covered = new HashSet<int>();

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            if (tile.IsNode && tile.Node != Root)
            {
                covered.Add(Tree.ParentOf(tile.Node));
            }
        }

        return covered;
    }

    /// <summary>
    /// A folder's name, centred in its band, which is one line of text with the gap shared above and
    /// below it. Along the band from where it comes on the canvas, and down from the band's own top,
    /// which moves with the folder as a zoom scrolls it away.
    /// </summary>
    private ExploreLabel HeaderLabel(Named named)
    {
        var (tile, shown) = named;

        return new ExploreLabel(
            tile.Node,
            shown.X + Limits.LabelPadding,
            tile.Y + ((tile.Header - Limits.MinimumLabelHeight) / 2),
            shown.Width - (Limits.LabelPadding * 2),
            Rotation: 0,
            Centred: false,
            TextColourFor(tile.Node, tile.Depth, tile.Framed),
            tile.Bytes,
            new LabelRoom(tile.X, tile.Y, tile.X + tile.Width, tile.Y + tile.Header));
    }

    /// <summary>A shape's name, at the top left of the part of it on the canvas.</summary>
    private ExploreLabel InsideLabel(Named named)
    {
        var (tile, shown) = named;

        return new ExploreLabel(
            tile.Node,
            shown.X + Limits.LabelPadding,
            shown.Y + (Limits.LabelPadding / 2),
            shown.Width - (Limits.LabelPadding * 2),
            Rotation: 0,
            Centred: false,
            TextColourFor(tile.Node, tile.Depth, tile.Framed),
            tile.Bytes,
            new LabelRoom(tile.X, tile.Y, tile.X + tile.Width, tile.Y + tile.Height));
    }

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

    /// <summary>Keeps at most <paramref name="count"/> of <paramref name="named"/>, largest shape on the canvas first.</summary>
    private static void Largest(List<Named> named, int count)
    {
        named.Sort(LargestFirst);

        if (named.Count > count)
        {
            named.RemoveRange(count, named.Count - count);
        }
    }

    /// <summary>
    /// The part of <paramref name="tile"/> on the canvas, which is all of it unless the picture is
    /// zoomed. Empty where none of it is.
    /// </summary>
    private ExploreTile OnCanvas(ExploreTile tile)
    {
        var left = Math.Max(0, tile.X);
        var top = Math.Max(0, tile.Y);
        var right = Math.Min(Width, tile.X + tile.Width);
        var bottom = Math.Min(Height, tile.Y + tile.Height);

        return tile with
        {
            X = left,
            Y = top,
            Width = Math.Max(0, right - left),
            Height = Math.Max(0, bottom - top),
        };
    }
}
