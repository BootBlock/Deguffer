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

    private readonly IReadOnlyList<ExploreTile> _tiles;
    private readonly TileHitTest _hits;

    /// <param name="viewport">
    /// The part of the picture <paramref name="tiles"/> were laid out for, or null where their layout
    /// draws only the whole of it. See <see cref="ExploreSurface.Viewport"/>.
    /// </param>
    /// <param name="volumeBeside">
    /// Whether the layout draws anything of the volume beside the root, whether or not this
    /// viewport shows it. See <see cref="ExploreSurface.HasVolumeBeside"/>.
    /// </param>
    public TiledSurface(
        ISizedTree tree,
        int root,
        int width,
        int height,
        LayoutLimits limits,
        ShapeColours colours,
        IReadOnlyList<ExploreTile> tiles,
        MapViewport? viewport = null,
        bool volumeBeside = false)
        : base(tree, root, width, height, limits, colours, viewport)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        _tiles = tiles;
        _hits = new TileHitTest(tiles, width, height);
        HasVolumeBeside = volumeBeside;

        Labels = BuildLabels();
    }

    public override IReadOnlyList<ExploreLabel> Labels { get; }

    public override bool HasVolumeBeside { get; }

    public override void Paint(byte[] pixels, TileColour background) =>
        TileRasteriser.Paint(pixels, _tiles, Width, Height, background, ColourFor);

    public override ExploreHit? At(float x, float y) =>
        _hits.At(x, y) is { } index ? new ExploreHit(_tiles[index].Node, _tiles[index].Bytes) : null;

    public override ExploreTile? TileAt(float x, float y) => _hits.At(x, y) is { } index ? _tiles[index] : null;

    public override IReadOnlyList<ExploreOutline> Outlines(IReadOnlySet<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var outlines = new List<ExploreOutline>();

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            if (!tile.IsNode || !nodes.Contains(tile.Node))
            {
                continue;
            }

            var right = tile.X + tile.Width;
            var bottom = tile.Y + tile.Height;

            outlines.Add(new ExploreOutline(tile.Node, [
                new ExplorePoint(tile.X, tile.Y),
                new ExplorePoint(right, tile.Y),
                new ExplorePoint(right, bottom),
                new ExplorePoint(tile.X, bottom),
            ]));
        }

        return outlines;
    }

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
        // Which nodes had a rectangle drawn inside them. The layouts emit a parent before its
        // children, so this is complete for every tile by the time the second pass reaches it.
        //
        // Both passes are indexed rather than foreached. A treemap of a real volume is tens of
        // thousands of rectangles, and enumerating an IReadOnlyList boxes the list's own struct
        // enumerator and dispatches every step through the interface — the same cost TileHitTest
        // dropped its iterator to avoid, on the same list and once per repaint (G5).
        var covered = new HashSet<int>();

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            if (tile.IsNode && tile.Node != Root)
            {
                covered.Add(Tree.ParentOf(tile.Node));
            }
        }

        var headers = new List<int>();
        var insides = new List<int>();

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
                    headers.Add(i);
                }
            }
            else if (!tile.IsAggregate
                && (tile.IsFreeSpace || (tile.Node != Root && !covered.Contains(tile.Node)))
                && shown.HasRoomForALabel(Limits))
            {
                insides.Add(i);
            }
        }

        var labels = new List<ExploreLabel>();

        foreach (var i in Largest(headers, MaximumHeaders))
        {
            var tile = _tiles[i];
            var shown = OnCanvas(tile);

            // Centred in the band, which is one line of text with the gap shared above and below it.
            // Along the band from where it comes on the canvas, and down from the band's own top,
            // which moves with the folder as a zoom scrolls it away.
            labels.Add(new ExploreLabel(
                tile.Node,
                shown.X + Limits.LabelPadding,
                tile.Y + ((tile.Header - Limits.MinimumLabelHeight) / 2),
                shown.Width - (Limits.LabelPadding * 2),
                Rotation: 0,
                Centred: false,
                TextColourFor(tile.Node, tile.Depth),
                tile.Bytes));
        }

        foreach (var i in Largest(insides, MaximumLabels))
        {
            var tile = _tiles[i];
            var shown = OnCanvas(tile);

            labels.Add(new ExploreLabel(
                tile.Node,
                shown.X + Limits.LabelPadding,
                shown.Y + (Limits.LabelPadding / 2),
                shown.Width - (Limits.LabelPadding * 2),
                Rotation: 0,
                Centred: false,
                TextColourFor(tile.Node, tile.Depth),
                tile.Bytes));
        }

        return labels;
    }

    /// <summary>At most <paramref name="count"/> of <paramref name="indices"/>, largest shape first.</summary>
    private List<int> Largest(List<int> indices, int count)
    {
        indices.Sort((a, b) => Area(OnCanvas(_tiles[b])).CompareTo(Area(OnCanvas(_tiles[a]))));

        if (indices.Count > count)
        {
            indices.RemoveRange(count, indices.Count - count);
        }

        return indices;
    }

    private static float Area(ExploreTile tile) => tile.Width * tile.Height;

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
