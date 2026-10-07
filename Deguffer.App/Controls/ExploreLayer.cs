using System.Runtime.InteropServices.WindowsRuntime;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Deguffer.App.Controls;

/// <summary>
/// One bitmap of a map, the drawing in it or on its way into it, and where it is placed. See
/// <see cref="ExploreLayers"/>, which decides which drawing goes in which.
/// </summary>
internal sealed class ExploreLayer
{
    public ExploreLayer() =>
        Image = new Image
        {
            // The bitmap is rendered at the display's pixel size and stretched back over the
            // control's logical size, so this maps one bitmap pixel to one device pixel rather
            // than resampling.
            Stretch = Stretch.Fill,
            RenderTransform = Placed,
        };

    public Image Image { get; }

    public CompositeTransform Placed { get; } = new();

    public WriteableBitmap? Bitmap { get; set; }

    /// <summary>The drawing in the bitmap, once all of it is there, or null.</summary>
    public ExploreSurface? Drawing { get; set; }

    /// <summary>The redraw landing in the bitmap, while it does, or null.</summary>
    public CanvasRedraw? Landing { get; set; }

    /// <summary>
    /// Whether the redraw landing here replaces the drawing on top, and so shows over it while it
    /// lands, rather than going under it.
    /// </summary>
    public bool LandsOnTop { get; set; }

    /// <summary>The part of the picture the drawing shows, or will once it has landed.</summary>
    public MapViewport Viewport { get; set; }

    /// <summary>Which picture the drawing is of, on the count <see cref="ExploreLayers"/> keeps.</summary>
    public int Picture { get; set; }

    /// <summary>When it was last shown, on the count <see cref="ExploreLayers"/> keeps.</summary>
    public long LastShown { get; set; }

    /// <summary>Whether anything of a drawing is in the bitmap to be seen.</summary>
    public bool IsShown => Drawing is not null || Landing is not null;

    /// <summary>
    /// Make every pixel of the bitmap transparent, so a drawing landing in it a region at a time shows
    /// whatever is under it until each region lands, rather than whatever the bitmap held last.
    /// </summary>
    public void Clear()
    {
        var bitmap = Bitmap!;
        var stride = bitmap.PixelWidth * 4;
        var nothing = new byte[stride];

        using var stream = bitmap.PixelBuffer.AsStream();

        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            stream.Write(nothing, 0, stride);
        }

        bitmap.Invalidate();
    }

    /// <summary>
    /// Copy <paramref name="regions"/> of <paramref name="pixels"/>, a canvas the bitmap's size, into
    /// the bitmap, and nothing else of it: the rest of the buffer is still being painted.
    ///
    /// <para>Through one stream over the bitmap's own memory, opened once per hand-over. A copy per
    /// row through the buffer extensions would look the memory up again for every row, and a 4K
    /// canvas is two thousand rows.</para>
    /// </summary>
    public void Land(byte[] pixels, IReadOnlyList<CanvasRegion> regions)
    {
        var bitmap = Bitmap!;
        var stride = bitmap.PixelWidth * 4;

        using var stream = bitmap.PixelBuffer.AsStream();

        foreach (var region in regions)
        {
            for (var y = region.Y; y < region.Bottom; y++)
            {
                var offset = (y * stride) + (region.X * 4);

                stream.Position = offset;
                stream.Write(pixels, offset, region.Width * 4);
            }
        }

        bitmap.Invalidate();
    }
}
