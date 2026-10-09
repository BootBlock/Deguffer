using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Controls;

/// <summary>
/// The one buffer every drawing of a map is painted into on the CPU, before each region of it is
/// written to the surface it is shown on.
///
/// <para>Shared by both of a map's sets of drawings rather than held by each (see
/// <see cref="ExploreLayers"/>), because only one drawing is ever being painted and the buffer is
/// 33 MB at 3840 by 2160 (G5).</para>
/// </summary>
internal sealed class PaintBuffers
{
    private byte[]? _pixels;

    /// <summary>A buffer for a canvas of this size, the one already held where it is that size.</summary>
    public byte[] PixelsFor(int width, int height)
    {
        var length = PixelBuffer.LengthFor(width, height);

        if (_pixels is not { } pixels || pixels.Length != length)
        {
            pixels = new byte[length];
            _pixels = pixels;
        }

        return pixels;
    }

    /// <summary>Let go of it, for a map with nothing left to paint.</summary>
    public void Release() => _pixels = null;
}
