using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Draws laid-out rectangles into a pixel buffer.
///
/// <para>A bitmap rather than one shaped element per rectangle, because a full volume lays out to
/// tens of thousands of them and the framework's own guidance is that a vector element repeated
/// enough times should become an image instead. It is also what the reference implementations do —
/// WinDirStat renders into a top-down DIB once and blits it, drawing only the selection live over
/// the cached frame.</para>
///
/// <para>In Core rather than in the shell because it is a pure function from rectangles to bytes,
/// with no window, no dispatcher and no theme object anywhere in it. What reaches the screen is
/// verifiable only by looking at it, so as little as possible is left to that (G8).</para>
///
/// <para>Text is deliberately not drawn here. Labels are laid over the finished bitmap as real
/// controls, which keeps them selectable, scalable with the user's text size, and visible to a
/// screen reader — none of which a label burnt into a bitmap is.</para>
/// </summary>
public sealed class TileRasteriser : CanvasPainter
{
    /// <summary>
    /// The shapes as an array rather than through the interface. Every region walks the whole list,
    /// so on a 4K canvas of thirty thousand rectangles that is a few million calls through an
    /// interface indexer returning a 32-byte struct, per repaint (G4). The layouts hand back arrays,
    /// so the copy is the fallback rather than the usual case.
    /// </summary>
    private readonly ExploreTile[] _shapes;

    private readonly TileColour[] _colours;

    /// <summary>
    /// Get ready to paint <paramref name="tiles"/> on a canvas of <paramref name="width"/> by
    /// <paramref name="height"/>.
    ///
    /// <para><paramref name="colourOf"/> answers what one shape is painted, given its node and its
    /// depth. Supplied rather than decided here because what a colour means is the surface's choice
    /// — a hue per branch, or a band per age — and because the tree does not know which node the
    /// view is currently rooted at, which is what a branch is measured from.</para>
    /// </summary>
    public TileRasteriser(
        IReadOnlyList<ExploreTile> tiles,
        int width,
        int height,
        TileColour background,
        Func<int, int, TileColour> colourOf)
        : base(width, height, background)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(colourOf);

        _shapes = tiles as ExploreTile[] ?? [.. tiles];

        // One colour per rectangle, before a single pixel is written, as SectorRasteriser does. Each
        // region walks the whole list, so resolving a colour inside that walk would climb a node's
        // ancestors once per region as well as once per rectangle (G4).
        _colours = new TileColour[_shapes.Length];

        for (var i = 0; i < _shapes.Length; i++)
        {
            _colours[i] = colourOf(_shapes[i].Node, _shapes[i].Depth);
        }
    }

    /// <summary>
    /// Paint the rectangles that show in <paramref name="region"/>.
    ///
    /// <para>Cut by region, not by rectangle. A treemap of a real volume is tens of thousands of
    /// small rectangles and a handful of large ones, so a partition drawn around each rectangle in
    /// turn leaves almost every one of them below any size worth handing to a second thread, and the
    /// canvas is shaded on one core while the rest sit idle (G4). A region owns its pixels outright,
    /// and every rectangle is offered to every region, clipped to it, so the picture is the one a
    /// single pass over the whole canvas would have produced.</para>
    ///
    /// <para>Tiles are walked from the end of the list towards the start, and the first shape to
    /// claim a pixel keeps it. That is the same picture as painting them in the order given, where
    /// a later shape covers an earlier one: whichever of two overlapping shapes comes later wins
    /// under both rules. What it avoids is shading a pixel once for every level above it — see
    /// <see cref="ClaimedPixels"/> for what that costs on a real volume.</para>
    /// </summary>
    protected override void Draw(byte[] pixels, CanvasRegion region)
    {
        var claimed = new ClaimedPixels(region);

        // Stopping the moment the region is entirely spoken for, for the reason the walk is
        // backwards: a shape can only show where nothing nested inside it already does.
        for (var i = _shapes.Length - 1; i >= 0 && !claimed.IsFull; i--)
        {
            Cushion(pixels, claimed, Width, Height, _shapes[i], _colours[i]);
        }
    }

    /// <summary>
    /// Shade one rectangle into whichever of <paramref name="claimed"/>'s pixels are still free.
    ///
    /// <para>The cushion is measured across the whole rectangle and only <em>drawn</em> where it
    /// shows. Measuring it within the region instead would restart the gradient at every region
    /// boundary and put a seam across the picture wherever one fell; measuring it across only the
    /// unclaimed part would stretch a shape's whole cushion into the sliver of it that is
    /// visible.</para>
    ///
    /// <para>The same holds at the canvas's edges. A zoomed picture's shapes run off them, and a
    /// cushion measured across only the part on the canvas would put a whole cushion in that part,
    /// then move it as the zoom did.</para>
    /// </summary>
    private static void Cushion(
        byte[] pixels,
        ClaimedPixels claimed,
        int width,
        int height,
        ExploreTile tile,
        TileColour colour)
    {
        var shapeLeft = (int)MathF.Round(tile.X);
        var shapeTop = (int)MathF.Round(tile.Y);
        var shapeRight = (int)MathF.Round(tile.X + tile.Width);
        var shapeBottom = (int)MathF.Round(tile.Y + tile.Height);

        var area = claimed.Region;
        var left = Math.Max(Math.Max(0, shapeLeft), area.X);
        var right = Math.Min(Math.Min(width, shapeRight), area.Right);
        var firstRow = Math.Max(Math.Max(0, shapeTop), area.Y);
        var lastRow = Math.Min(Math.Min(height, shapeBottom), area.Bottom);

        if (right <= left || lastRow <= firstRow)
        {
            return;
        }

        var ridge = CushionShading.RidgeAt(tile.Depth);

        // Three shapes are drawn flat. Neither block is a thing on the disk, and a cushion would give
        // it the same physical presence as the files it stands for. A folder with a band has its
        // name written across its top edge, where a cushion is at its brightest on the left and its
        // darkest on the right, so text chosen to contrast with the folder's colour would be chosen
        // against a colour the band is not. The band and the gap already say where the folder is,
        // which is the job the cushion was doing.
        if (!tile.IsNode || tile.Header > 0)
        {
            ridge = 0;
        }

        var spanWidth = shapeRight - shapeLeft;
        var spanHeight = shapeBottom - shapeTop;

        for (var y = firstRow; y < lastRow; y++)
        {
            var v = spanHeight <= 1 ? 0.5 : (double)(y - shapeTop) / (spanHeight - 1);
            var ny = ridge * ((2 * v) - 1);
            var offset = ((y * width) + left) * 4;

            for (var x = left; x < right; x++)
            {
                if (claimed.Claim(x, y))
                {
                    var u = spanWidth <= 1 ? 0.5 : (double)(x - shapeLeft) / (spanWidth - 1);
                    var nx = ridge * ((2 * u) - 1);

                    CushionShading.Write(pixels, offset, colour, CushionShading.LightAt(nx, ny));
                }

                offset += 4;
            }
        }
    }
}
