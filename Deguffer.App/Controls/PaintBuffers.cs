using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Deguffer.App.Controls;

/// <summary>
/// The memory a map paints through: the one buffer every drawing is painted into before it is copied
/// to its bitmap, and one bitmap no drawing is using, kept for the next drawing to need one.
///
/// <para>Shared by both of a map's sets of drawings rather than held by each (see
/// <see cref="ExploreLayers"/>), because only one drawing is ever being painted and each of these is
/// 33 MB at 3840 by 2160 (G5). The spare bitmap is what a folder opening paints into: the set it opens
/// into was emptied at the end of the last opening, and without it every opening would allocate a new
/// bitmap while dropping up to three. One is kept rather than all of them, because a bitmap's pixels
/// are native memory the garbage collector does not count, and a pool that kept every one would hold
/// a whole picture's worth for nothing.</para>
/// </summary>
internal sealed class PaintBuffers
{
    private byte[]? _pixels;

    private WriteableBitmap? _spare;

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

    /// <summary>A bitmap of this size: the spare, where it is this size, and a new one otherwise.</summary>
    public WriteableBitmap BitmapFor(int width, int height)
    {
        var spare = _spare;

        _spare = null;

        return spare is not null && spare.PixelWidth == width && spare.PixelHeight == height
            ? spare
            : new WriteableBitmap(width, height);
    }

    /// <summary>Keep <paramref name="bitmap"/> for the next drawing, in place of any spare already kept.</summary>
    public void Return(WriteableBitmap bitmap) => _spare = bitmap;

    /// <summary>Let go of both, for a map with nothing left to paint.</summary>
    public void Release()
    {
        _pixels = null;
        _spare = null;
    }
}
