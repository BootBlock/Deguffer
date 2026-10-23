using Deguffer.Core.Configuration;

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

    /// <summary>How every quad is finished. See <see cref="CushionQuads.Finish"/>.</summary>
    private readonly ShapeFinish _finish;

    /// <summary>
    /// Get ready to paint <paramref name="quads"/> on <paramref name="background"/>, on a canvas of
    /// their size.
    ///
    /// <para>The colours and the finish are the quads': see <see cref="CushionQuads"/> for why the two
    /// routes to the screen share them rather than each working them out.</para>
    /// </summary>
    public TileRasteriser(CushionQuads quads, TileColour background)
        : base(quads.Width, quads.Height, background)
    {
        _quads = quads.Array;
        _finish = quads.Finish;
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
            Paint(pixels, claimed, Width, Height, _quads[i], _finish);
        }
    }

    /// <summary>
    /// Paint one quad, finished as <paramref name="finish"/> says, into whichever of
    /// <paramref name="claimed"/>'s pixels are still free: its line inside its sides, then its bevel
    /// inside that, then its cushion, its sheen or its own colour across the rest.
    ///
    /// <para>A side off the canvas is not drawn, nor its line or its bevel: the shape goes on past it.
    /// Where two sides meet, the bevel's lit side is the top or the left only where it is nearer than
    /// both the bottom and the right, so a corner where one of each meets is shaded, as a raised button
    /// is drawn. The shader decides every pixel by the same rule.</para>
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
    private static void Paint(byte[] pixels, ClaimedPixels claimed, int width, int height, CushionQuad quad, ShapeFinish finish)
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

        var line = finish.Borders.WidthFor(quad.Framed);
        var lineColour = line > 0 ? ShapeFinish.Mix(quad.Colour, finish.Borders.Tone, finish.Borders.AmountFor(quad.Framed)) : default;
        var bevelled = finish.Shading == MapShading.Bevel && quad.Ridge > 0;
        var band = line + (bevelled ? finish.Bevel.Width : 0);
        var lit = bevelled ? finish.Lit(quad.Colour) : default;
        var shaded = bevelled ? finish.Shaded(quad.Colour) : default;
        var cushioned = finish.Shading == MapShading.Cushion;
        var sheened = finish.Shading == MapShading.Gradient && quad.Ridge > 0;
        var slope = sheened ? GradientShading.SlopeAt(finish.Relief) : 0;

        var ridge = (double)quad.Ridge;
        var spanWidth = quad.Right - quad.Left;
        var spanHeight = quad.Bottom - quad.Top;

        for (var y = firstRow; y < lastRow; y++)
        {
            var fromTop = y - quad.Top;
            var fromBottom = quad.Bottom - 1 - y;
            var v = spanHeight <= 1 ? 0.5 : (double)fromTop / (spanHeight - 1);
            var ny = ridge * ((2 * v) - 1);
            var sheen = sheened ? GradientShading.Lit(quad.Colour, GradientShading.LightAt(slope, fromTop, spanHeight - 1)) : default;
            var offset = ((y * width) + left) * 4;

            for (var x = left; x < right; x++)
            {
                if (claimed.Claim(x, y))
                {
                    var fromLeft = x - quad.Left;
                    var fromRight = quad.Right - 1 - x;
                    var nearest = Math.Min(Math.Min(fromLeft, fromRight), Math.Min(fromTop, fromBottom));

                    if (nearest < line)
                    {
                        PixelBuffer.Write(pixels, offset, lineColour);
                    }
                    else if (nearest < band)
                    {
                        PixelBuffer.Write(pixels, offset, Math.Min(fromLeft, fromTop) < Math.Min(fromRight, fromBottom) ? lit : shaded);
                    }
                    else if (cushioned)
                    {
                        var u = spanWidth <= 1 ? 0.5 : (double)fromLeft / (spanWidth - 1);
                        var nx = ridge * ((2 * u) - 1);

                        CushionShading.Write(pixels, offset, quad.Colour, CushionShading.LightAt(nx, ny));
                    }
                    else if (sheened)
                    {
                        PixelBuffer.Write(pixels, offset, sheen);
                    }
                    else
                    {
                        PixelBuffer.Write(pixels, offset, quad.Colour);
                    }
                }

                offset += 4;
            }
        }
    }
}
