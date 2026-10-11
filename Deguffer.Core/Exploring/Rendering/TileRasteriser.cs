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
    /// The quads as an array rather than through a list. Every region walks all of them, so on a 4K
    /// canvas of thirty thousand rectangles that is a few million reads per repaint (G4).
    /// </summary>
    private readonly CushionQuad[] _quads;

    /// <summary>
    /// Get ready to paint <paramref name="quads"/> on <paramref name="background"/>, on a canvas of
    /// their size.
    ///
    /// <para>The colours and the edges are the quads': see <see cref="CushionQuads"/> for why the two
    /// routes to the screen share them rather than each working them out.</para>
    /// </summary>
    public TileRasteriser(CushionQuads quads, TileColour background)
        : base(quads.Width, quads.Height, background)
    {
        _quads = quads.Array;
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
    /// <para>Quads are walked from the end of the list towards the start, and the first to claim a
    /// pixel keeps it. That is the same picture as painting them in the order given, where a later
    /// quad covers an earlier one: whichever of two overlapping quads comes later wins under both
    /// rules. What it avoids is shading a pixel once for every level above it — see
    /// <see cref="ClaimedPixels"/> for what that costs on a real volume.</para>
    /// </summary>
    protected override void Draw(byte[] pixels, CanvasRegion region)
    {
        var claimed = new ClaimedPixels(region);

        // Stopping the moment the region is entirely spoken for, for the reason the walk is
        // backwards: a shape can only show where nothing nested inside it already does.
        for (var i = _quads.Length - 1; i >= 0 && !claimed.IsFull; i--)
        {
            Cushion(pixels, claimed, Width, Height, _quads[i]);
        }
    }

    /// <summary>
    /// Shade one quad into whichever of <paramref name="claimed"/>'s pixels are still free.
    ///
    /// <para>The cushion is measured across the whole quad and only <em>drawn</em> where it shows.
    /// Measuring it within the region instead would restart the gradient at every region boundary and
    /// put a seam across the picture wherever one fell; measuring it across only the unclaimed part
    /// would stretch a shape's whole cushion into the sliver of it that is visible.</para>
    ///
    /// <para>The same holds at the canvas's edges. A zoomed picture's shapes run off them, and a
    /// cushion measured across only the part on the canvas would put a whole cushion in that part,
    /// then move it as the zoom did.</para>
    /// </summary>
    private static void Cushion(byte[] pixels, ClaimedPixels claimed, int width, int height, CushionQuad quad)
    {
        var area = claimed.Region;
        var left = Math.Max(Math.Max(0, quad.Left), area.X);
        var right = Math.Min(Math.Min(width, quad.Right), area.Right);
        var firstRow = Math.Max(Math.Max(0, quad.Top), area.Y);
        var lastRow = Math.Min(Math.Min(height, quad.Bottom), area.Bottom);

        if (right <= left || lastRow <= firstRow)
        {
            return;
        }

        var ridge = (double)quad.Ridge;
        var spanWidth = quad.Right - quad.Left;
        var spanHeight = quad.Bottom - quad.Top;

        for (var y = firstRow; y < lastRow; y++)
        {
            var v = spanHeight <= 1 ? 0.5 : (double)(y - quad.Top) / (spanHeight - 1);
            var ny = ridge * ((2 * v) - 1);
            var offset = ((y * width) + left) * 4;

            for (var x = left; x < right; x++)
            {
                if (claimed.Claim(x, y))
                {
                    var u = spanWidth <= 1 ? 0.5 : (double)(x - quad.Left) / (spanWidth - 1);
                    var nx = ridge * ((2 * u) - 1);

                    CushionShading.Write(pixels, offset, quad.Colour, CushionShading.LightAt(nx, ny));
                }

                offset += 4;
            }
        }
    }
}
