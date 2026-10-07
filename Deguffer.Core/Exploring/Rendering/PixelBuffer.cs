using System.Runtime.InteropServices;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The BGRA buffer both rasterisers draw into, and how the work over it is spread when a whole
/// canvas is painted at once.
///
/// <para>Separate from either of them because it is the part that is the same whatever shape is
/// being drawn: how large a canvas's buffer is, what an undrawn pixel holds, and when a span of
/// rows is worth handing to more than one thread.</para>
/// </summary>
public static class PixelBuffer
{
    /// <summary>
    /// Rows big enough to be worth splitting across threads. Below this the scheduling costs more
    /// than the shading (G4).
    /// </summary>
    private const int ParallelPixelThreshold = 512 * 1024;

    /// <summary>
    /// How many bands to cut a span of rows into, per thread available.
    ///
    /// <para>More bands than threads, because the work in a band is not uniform: a band across the
    /// middle of a treemap holds far more rectangles than one along its bottom edge, and a sunburst
    /// leaves the corners of its canvas empty. <see cref="Parallel.For(int, int, ParallelOptions,
    /// Action{int})"/> can only even that out where there are spare pieces left to hand to whichever
    /// thread finishes first.</para>
    /// </summary>
    private const int BandsPerThread = 4;

    /// <summary>
    /// G4: bounded explicitly rather than left to the pool. This is not a rare branch — the root
    /// shape of any map on a display wider than 1024 by 512 clears the threshold on every repaint,
    /// and a repaint happens per progress snapshot, per view change, per descend and once a window
    /// edge has settled. Processor count rather than twice it, because shading is arithmetic and
    /// waits on nothing.
    /// </summary>
    private static readonly ParallelOptions RowOptions = new()
    {
        MaxDegreeOfParallelism = Environment.ProcessorCount,
    };

    /// <summary>How large a buffer a canvas of this size needs.</summary>
    public static int LengthFor(int width, int height) => width * height * 4;

    /// <summary>
    /// Overwrite <paramref name="region"/> of a canvas <paramref name="width"/> pixels across with
    /// one opaque colour.
    /// </summary>
    public static void Fill(byte[] pixels, int width, CanvasRegion region, TileColour colour)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        var packed = Packed(colour);
        var words = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());

        // Row by row unless the region is whole rows, where it is one span and one vectorised fill.
        if (region.X == 0 && region.Width == width)
        {
            words.Slice(region.Y * width, region.Height * width).Fill(packed);
            return;
        }

        for (var y = region.Y; y < region.Bottom; y++)
        {
            words.Slice((y * width) + region.X, region.Width).Fill(packed);
        }
    }

    /// <summary>
    /// Cut rows <paramref name="top"/> up to but not including <paramref name="bottom"/> into bands
    /// and hand each to <paramref name="paint"/>, across threads where <paramref name="pixels"/>
    /// says there is enough work to pay for it.
    ///
    /// <para>A band owns its rows outright and no two overlap, so a caller that draws overlapping
    /// shapes still gets each pixel written by one thread, in the order it hands the shapes over
    /// (G4).</para>
    /// </summary>
    public static void Bands(int top, int bottom, int pixels, Action<int, int> paint)
    {
        ArgumentNullException.ThrowIfNull(paint);

        var rows = bottom - top;

        if (rows <= 0)
        {
            return;
        }

        if (pixels < ParallelPixelThreshold || rows == 1)
        {
            paint(top, bottom);
            return;
        }

        // Never more bands than rows, so every band holds at least one row and none of them is
        // handed an empty span.
        var bands = Math.Min(rows, RowOptions.MaxDegreeOfParallelism * BandsPerThread);

        Parallel.For(0, bands, RowOptions, band =>
        {
            // From the band number rather than by accumulating a height, so one band's end is the
            // same expression as the next one's start. That is what makes the bands tile the span
            // exactly, with no gap and no overlap, however the division rounds — and the last one
            // ends on `bottom` rather than a rounding error short of it.
            paint(top + (rows * band / bands), top + (rows * (band + 1) / bands));
        });
    }

    /// <summary>
    /// One BGRA pixel as the single word a vectorised fill can write.
    ///
    /// <para>Assembled through the buffer rather than by shifting, so it holds whatever the machine
    /// reading those four bytes back would read and does not assume a byte order.</para>
    /// </summary>
    private static uint Packed(TileColour colour)
    {
        Span<byte> pixel = [colour.Blue, colour.Green, colour.Red, 255];

        return MemoryMarshal.Read<uint>(pixel);
    }
}
