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
    private readonly IReadOnlyList<ExploreTile> _tiles;
    private readonly TileHitTest _hits;

    public TiledSurface(
        ExploreTree tree,
        int root,
        int width,
        int height,
        LayoutLimits limits,
        ExploreColouring colouring,
        DateTime nowUtc,
        IReadOnlyList<ExploreTile> tiles)
        : base(tree, root, width, height, limits, colouring, nowUtc)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        _tiles = tiles;
        _hits = new TileHitTest(tiles, width, height);

        Labels = BuildLabels();
    }

    public override IReadOnlyList<ExploreLabel> Labels { get; }

    public override void Paint(byte[] pixels, TileColour background) =>
        TileRasteriser.Paint(pixels, _tiles, Width, Height, background, ColourFor);

    public override ExploreHit? At(float x, float y) =>
        _hits.At(x, y) is { } index ? new ExploreHit(_tiles[index].Node, _tiles[index].Bytes) : null;

    /// <summary>
    /// Lay the labels over the finished bitmap.
    ///
    /// <para>Only a rectangle with nothing drawn inside it gets one. A child is inset from its
    /// parent by a single pixel, so a parent's label and its first child's land within two pixels of
    /// each other and overprint into an unreadable stack — which is what the top-left corner of a
    /// treemap of any real drive looked like. Labelling the innermost rectangles instead is both
    /// legible and the more useful half: the parent's name is on the breadcrumb, and what is inside
    /// it is not written anywhere else.</para>
    /// </summary>
    private IReadOnlyList<ExploreLabel> BuildLabels()
    {
        // Which nodes had a rectangle drawn inside them. The layouts emit a parent before its
        // children, so this is complete for every tile by the time the second pass reaches it.
        var covered = new HashSet<int>();

        foreach (var tile in _tiles)
        {
            if (!tile.IsAggregate && tile.Node != Root)
            {
                covered.Add(Tree.ParentOf(tile.Node));
            }
        }

        var labels = new List<ExploreLabel>();

        foreach (var tile in _tiles)
        {
            if (labels.Count >= MaximumLabels)
            {
                break;
            }

            if (tile.IsAggregate
                || tile.Node == Root
                || covered.Contains(tile.Node)
                || !tile.HasRoomForALabel(Limits))
            {
                continue;
            }

            labels.Add(new ExploreLabel(
                tile.Node,
                tile.X + Limits.LabelPadding,
                tile.Y + (Limits.LabelPadding / 2),
                tile.Width - (Limits.LabelPadding * 2),
                Rotation: 0,
                Centred: false,
                TextColourFor(tile.Node, tile.Depth)));
        }

        return labels;
    }
}
