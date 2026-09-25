using System.Runtime.InteropServices.WindowsRuntime;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Deguffer.App.Controls;

/// <summary>
/// The drawings a map keeps of the one picture it shows: the whole of it, and the last parts of it a
/// zoom stopped at, each a bitmap placed where its part of the picture is on the screen.
///
/// <para>Kept so a move has something to show wherever it goes. A zoom animates by moving and
/// magnifying the drawing on hand, and with only that one a zoom out shrank it into an empty frame,
/// and a drag pulled it away from an empty edge, until the move stopped and the picture was drawn
/// again. The whole picture sits under everything, so the screen is never bare: what shows at an
/// edge is at the wrong resolution until the move stops, but it is in the right place, because a
/// treemap lays a shape out once and only magnifies it after (see <see cref="TreemapDetail"/>).</para>
///
/// <para>Kept also so a zoom that comes back to a part of the picture already drawn shows that drawing
/// again rather than laying it out and painting it a second time. All the way out is the one every
/// zoom comes back to, and it costs nothing.</para>
///
/// <para>The drawing the map works from is on top. The others are under it by how far they are
/// zoomed, finest uppermost, so where one runs out the next finest shows through. At rest the one on
/// top covers the whole screen, because a viewport never shows past the picture's edge, so what a
/// click picks is always what it drew (§7.1).</para>
///
/// <para>Separate from <see cref="ExploreMap"/>, which decides what is drawn and what the pointer is
/// over; this holds bitmaps and places them, and knows nothing of trees (G1).</para>
/// </summary>
internal sealed class ExploreLayers
{
    /// <summary>
    /// How many zoomed drawings to keep besides the whole picture, the one on screen included.
    ///
    /// <para>Each is a bitmap the size of the canvas, which is 33 MB at 3840 by 2160, so this trades
    /// memory for the moves that come back to where they were. Two keeps the last place a zoom or a
    /// drag stopped at under the one it stops at next, which is the one most likely to show through
    /// as the reader goes back.</para>
    /// </summary>
    private const int ZoomedKept = 2;

    /// <summary>
    /// Where the drawing on top is stacked: above the finest any other can be (see
    /// <see cref="Fineness"/>). Not <see cref="int.MaxValue"/>, which the framework refuses with an
    /// argument error, because a z-index runs to a million at most.
    /// </summary>
    private const int Uppermost = (int)(MapViewport.MaximumZoom * 1000) + 1;

    private readonly Grid _panel = new() { IsHitTestVisible = false };

    /// <summary>Where the whole panel is carried to while an opened folder grows over it. Kept (G5).</summary>
    private readonly CompositeTransform _carried = new();

    private readonly List<Layer> _layers = [];

    private readonly PixelScratch _scratch;

    /// <summary>A count of the drawings shown, so the least recently shown is the first to go.</summary>
    private long _shown;

    private Layer? _current;

    public ExploreLayers(PixelScratch scratch)
    {
        _scratch = scratch;
        _panel.RenderTransform = _carried;
    }

    public UIElement Element => _panel;

    /// <summary>The drawing on top, which is the one the screen is working from.</summary>
    public ExploreSurface? Current => _current?.Drawing;

    /// <summary>
    /// Show the drawing of <paramref name="viewport"/>: the one already made, where there is one, and
    /// otherwise one <paramref name="draw"/> makes and this paints on <paramref name="ground"/>.
    ///
    /// <para>A zoomed drawing brings the whole picture with it, drawn first where it is not already
    /// kept, so there is always one under it.</para>
    /// </summary>
    public ExploreSurface Show(MapViewport viewport, Func<MapViewport, ExploreSurface> draw, TileColour ground)
    {
        ArgumentNullException.ThrowIfNull(draw);

        if (Kept(viewport) is not { } layer)
        {
            var drawing = draw(viewport);

            if (drawing.Viewport is { IsWhole: false } && Kept(MapViewport.Whole) is null)
            {
                Paint(draw(MapViewport.Whole), ground);
            }

            layer = Paint(drawing, ground);
        }

        layer.Shown = ++_shown;
        _current = layer;

        Release();

        return layer.Drawing!;
    }

    /// <summary>
    /// Stop showing any drawing, for a picture that has changed: another tree, colours, size or theme.
    /// The bitmaps stay for the next drawings to paint into, and go if nothing does.
    /// </summary>
    public void Forget()
    {
        foreach (var layer in _layers)
        {
            layer.Drawing = null;
            layer.Image.Visibility = Visibility.Collapsed;
        }

        _current = null;
    }

    /// <summary>Drop every drawing and bitmap, for a map with nothing to show.</summary>
    public void Clear()
    {
        Forget();
        Release();
    }

    /// <summary>
    /// Put each drawing where its part of the picture is on a screen <paramref name="width"/> by
    /// <paramref name="height"/> showing <paramref name="shown"/>, and the one being worked from on top.
    /// </summary>
    public void Place(MapViewport shown, double width, double height)
    {
        foreach (var layer in _layers)
        {
            if (layer.Drawing is null)
            {
                continue;
            }

            var placement = shown.PlacementOf(layer.Viewport);

            layer.Placed.ScaleX = placement.Scale;
            layer.Placed.ScaleY = placement.Scale;
            layer.Placed.TranslateX = placement.X * width;
            layer.Placed.TranslateY = placement.Y * height;

            Canvas.SetZIndex(layer.Image, layer == _current ? Uppermost : Fineness(layer.Viewport));
        }
    }

    /// <summary>
    /// Carry all of it so the screen lands on <paramref name="screen"/>, at
    /// <paramref name="opacity"/>, while a folder that was opened grows over the picture or out of it.
    /// </summary>
    public void Carry(MapFrame screen, double opacity, double width, double height)
    {
        _carried.ScaleX = screen.Width;
        _carried.ScaleY = screen.Height;
        _carried.TranslateX = screen.X * width;
        _carried.TranslateY = screen.Y * height;
        _panel.Opacity = opacity;
    }

    /// <summary>
    /// Where a drawing at <paramref name="viewport"/> is stacked among the others: finer higher. The
    /// zoom runs to <see cref="MapViewport.MaximumZoom"/>, and a thousand steps a doubling keeps two
    /// zooms a rounding error apart in order.
    /// </summary>
    private static int Fineness(MapViewport viewport) => (int)(viewport.Zoom * 1000);

    /// <summary>The drawing already made of <paramref name="viewport"/>, or null.</summary>
    private Layer? Kept(MapViewport viewport)
    {
        foreach (var layer in _layers)
        {
            if (layer.Drawing is not null && layer.Viewport == viewport)
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>
    /// Paint <paramref name="drawing"/> into a bitmap: one no drawing is using, or where every one is,
    /// the zoomed one least recently shown, never the whole picture or the one on top.
    /// </summary>
    private Layer Paint(ExploreSurface drawing, TileColour ground)
    {
        var viewport = drawing.Viewport ?? MapViewport.Whole;
        var layer = Free(drawing.Width, drawing.Height);

        if (layer is null && !viewport.IsWhole)
        {
            var zoomed = _layers.Where(kept => kept.Drawing is not null && !kept.Viewport.IsWhole).ToList();

            if (zoomed.Count >= ZoomedKept)
            {
                layer = zoomed.Where(kept => kept != _current).MinBy(kept => kept.Shown);
            }
        }

        if (layer is null)
        {
            layer = new Layer();
            _layers.Add(layer);
            _panel.Children.Add(layer.Image);
        }

        // Reused while the size holds. A scan redraws a map every three quarters of a second, and at
        // 3840 by 2160 each bitmap is 33 MB, so a new one per drawing would be megabytes of garbage a
        // second for a surface whose size only changes with the window's (G5).
        if (layer.Bitmap is not { } bitmap || bitmap.PixelWidth != drawing.Width || bitmap.PixelHeight != drawing.Height)
        {
            bitmap = new WriteableBitmap(drawing.Width, drawing.Height);
            layer.Bitmap = bitmap;
            layer.Image.Source = bitmap;
        }

        var pixels = _scratch.For(drawing.Width, drawing.Height);

        drawing.Paint(pixels, ground);
        pixels.CopyTo(0, bitmap.PixelBuffer, 0, pixels.Length);
        bitmap.Invalidate();

        layer.Drawing = drawing;
        layer.Viewport = viewport;
        layer.Shown = ++_shown;
        layer.Image.Visibility = Visibility.Visible;

        return layer;
    }

    /// <summary>A bitmap no drawing is using, of this size where there is one.</summary>
    private Layer? Free(int width, int height)
    {
        Layer? other = null;

        foreach (var layer in _layers)
        {
            if (layer.Drawing is not null)
            {
                continue;
            }

            if (layer.Bitmap is { } bitmap && bitmap.PixelWidth == width && bitmap.PixelHeight == height)
            {
                return layer;
            }

            other ??= layer;
        }

        return other;
    }

    /// <summary>Let go of every bitmap no drawing is using, which is memory nothing will show.</summary>
    private void Release()
    {
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            if (_layers[i].Drawing is null)
            {
                _panel.Children.Remove(_layers[i].Image);
                _layers.RemoveAt(i);
            }
        }
    }

    /// <summary>One bitmap, the drawing in it, and where it is placed.</summary>
    private sealed class Layer
    {
        public Layer() =>
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

        /// <summary>The drawing in the bitmap, or null where it is free for the next one.</summary>
        public ExploreSurface? Drawing { get; set; }

        /// <summary>The part of the picture the drawing shows.</summary>
        public MapViewport Viewport { get; set; }

        /// <summary>When it was last shown, on the count <see cref="_shown"/> keeps.</summary>
        public long Shown { get; set; }
    }
}

/// <summary>
/// The one buffer every drawing of a map is painted into before it is copied to its bitmap. Shared by
/// the map's layers rather than one each, because only one drawing is ever being painted, and at
/// 3840 by 2160 it is 33 MB of large-object heap (G5).
/// </summary>
internal sealed class PixelScratch
{
    private byte[]? _pixels;

    /// <summary>A buffer for a canvas of this size, the one already held where it is that size.</summary>
    public byte[] For(int width, int height)
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
