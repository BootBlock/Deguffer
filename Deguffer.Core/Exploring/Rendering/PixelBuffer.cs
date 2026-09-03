namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The BGRA buffer both rasterisers draw into, and how the work over it is spread.
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
    /// G4: bounded explicitly rather than left to the pool. This is not a rare branch — the root
    /// shape of any map on a display wider than 1024 by 512 clears the threshold on every repaint,
    /// and a repaint happens per progress snapshot, per view change, per descend and continuously
    /// while a window edge is dragged. Processor count rather than twice it, because shading is
    /// arithmetic and waits on nothing.
    /// </summary>
    private static readonly ParallelOptions RowOptions = new()
    {
        MaxDegreeOfParallelism = Environment.ProcessorCount,
    };

    /// <summary>How large a buffer a canvas of this size needs.</summary>
    public static int LengthFor(int width, int height) => width * height * 4;

    /// <summary>Overwrite the whole buffer with one opaque colour.</summary>
    public static void Fill(byte[] pixels, TileColour colour)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = colour.Blue;
            pixels[i + 1] = colour.Green;
            pixels[i + 2] = colour.Red;
            pixels[i + 3] = 255;
        }
    }

    /// <summary>
    /// Paint rows <paramref name="top"/> up to but not including <paramref name="bottom"/>, across
    /// threads where <paramref name="pixels"/> says there is enough work to pay for it.
    /// </summary>
    public static void Rows(int top, int bottom, int pixels, Action<int> paint)
    {
        if (pixels >= ParallelPixelThreshold)
        {
            Parallel.For(top, bottom, RowOptions, paint);
            return;
        }

        for (var y = top; y < bottom; y++)
        {
            paint(y);
        }
    }
}
