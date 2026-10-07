using System.Runtime.InteropServices;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The BGRA buffer both rasterisers draw into: how large a canvas's buffer is, and what an undrawn
/// pixel holds.
///
/// <para>Separate from either of them because it is the part that is the same whatever shape is
/// being drawn.</para>
/// </summary>
public static class PixelBuffer
{
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
