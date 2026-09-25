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
/// zoom or a drag stopped at, each a bitmap placed where its part of the picture is on the screen.
///
/// <para>Kept so a move has something to show wherever it goes. A zoom animates by moving and
/// magnifying the drawing on hand, and with only that one a zoom out shrank it into an empty frame,
/// and a drag pulled it away from an empty edge, until the move stopped and the picture was drawn
/// again. The whole picture sits under everything, so the screen is never bare: what shows at an
/// edge is at the wrong resolution until the move stops, but it is in the right place, because a
/// treemap lays a shape out once and only magnifies it after (see <see cref="TreemapDetail"/>).</para>
///
/// <para>Kept also so a move that comes back to a part of the picture already drawn shows that drawing
/// again rather than laying it out and painting it a second time. A zoom all the way out always
/// does, and so does a double-click on a shape already zoomed to once; a wheel or a drag rarely lands
/// on exactly the same part twice.</para>
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
    /// <para>Each is a bitmap the size of the canvas, which is 33 MB at 3840 by 2160. Two keeps the
    /// last place a zoom or a drag stopped at under the one it stops at next: finer than the whole
    /// picture where the reader goes back over it, and shown again outright where they return to
    /// exactly that place.</para>
    /// </summary>
    private const int ZoomedKept = 2;

    private readonly Grid _panel = new() { IsHitTestVisible = false };

    /// <summary>Where the whole panel is carried to while an opened folder grows over it. Kept (G5).</summary>
    private readonly CompositeTransform _carried = new();

    private readonly List<Layer> _layers = [];

    private readonly PaintBuffers _buffers;

    /// <summary>A count of the drawings shown, so the least recently shown is the first to go.</summary>
    private long _showings;

    private Layer? _current;

    public ExploreLayers(PaintBuffers buffers)
    {
        _buffers = buffers;
        _panel.RenderTransform = _carried;
    }

    public UIElement Element => _panel;

    /// <summary>
    /// Show the drawing of <paramref name="viewport"/>: the one already made, where there is one, and
    /// otherwise one <paramref name="draw"/> makes and this paints on <paramref name="ground"/>.
    /// </summary>
    public ExploreSurface Show(MapViewport viewport, Func<MapViewport, ExploreSurface> draw, TileColour ground)
    {
        var layer = Kept(viewport) ?? Paint(draw(viewport), ground);

        layer.LastShown = ++_showings;
        _current = layer;

        Release();

        return layer.Drawing!;
    }

    /// <summary>
    /// Paint the whole picture under the drawing on top, where that one is zoomed and the whole
    /// picture is not already kept. Says whether it painted.
    ///
    /// <para>Separate from <see cref="Show"/>, so the drawing the reader asked for is on screen first
    /// and this one, which only shows once the picture moves, is painted after it.</para>
    /// </summary>
    public bool Underlay(Func<MapViewport, ExploreSurface> draw, TileColour ground)
    {
        if (_current is not { Viewport.IsWhole: false } || Kept(MapViewport.Whole) is not null)
        {
            return false;
        }

        Paint(draw(MapViewport.Whole), ground);

        return true;
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

    /// <summary>
    /// Drop every drawing, for a map with nothing to show or nobody to show it to. The last bitmap
    /// goes back to the buffers for the next drawing.
    /// </summary>
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

            Canvas.SetZIndex(layer.Image, Rank(layer));
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
    /// Where <paramref name="layer"/> is stacked: the one on top above all the rest, and each other
    /// above every one coarser than it, or as fine and shown less recently. A rank rather than the
    /// zoom itself, because two drawings a drag apart share a zoom and would share a place, and
    /// which of them showed through would then be the panel's choice.
    /// </summary>
    private int Rank(Layer layer)
    {
        var rank = 0;

        foreach (var other in _layers)
        {
            if (other == layer || other.Drawing is null)
            {
                continue;
            }

            if (layer == _current
                || (other != _current
                    && (other.Viewport.Zoom < layer.Viewport.Zoom
                        || (other.Viewport.Zoom == layer.Viewport.Zoom && other.LastShown < layer.LastShown))))
            {
                rank++;
            }
        }

        return rank;
    }

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
                layer = zoomed.Where(kept => kept != _current).MinBy(kept => kept.LastShown);
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
            bitmap = _buffers.BitmapFor(drawing.Width, drawing.Height);
            layer.Bitmap = bitmap;
            layer.Image.Source = bitmap;
        }

        var pixels = _buffers.PixelsFor(drawing.Width, drawing.Height);

        drawing.Paint(pixels, ground);
        pixels.CopyTo(0, bitmap.PixelBuffer, 0, pixels.Length);
        bitmap.Invalidate();

        layer.Drawing = drawing;
        layer.Viewport = viewport;
        layer.LastShown = ++_showings;
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

    /// <summary>
    /// Take out every layer no drawing is using, which is memory nothing will show, and hand its
    /// bitmap back to the buffers for the next drawing.
    /// </summary>
    private void Release()
    {
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];

            if (layer.Drawing is not null)
            {
                continue;
            }

            if (layer.Bitmap is { } bitmap)
            {
                layer.Image.Source = null;
                _buffers.Return(bitmap);
            }

            _panel.Children.Remove(layer.Image);
            _layers.RemoveAt(i);
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

        /// <summary>When it was last shown, on the count <see cref="_showings"/> keeps.</summary>
        public long LastShown { get; set; }
    }
}
