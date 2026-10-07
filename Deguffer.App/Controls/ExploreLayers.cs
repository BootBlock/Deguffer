using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
/// <para>A new drawing lands a region at a time (see <see cref="CanvasRedraw"/>), into a bitmap of
/// its own over the rest, so the drawings already here go on showing round the regions that have not
/// landed yet. A picture that changed leaves its drawings on screen in the same way until the new
/// one has landed, and only then are they let go.</para>
///
/// <para>The drawing the map works from is on top, under only a drawing landing to replace it. The
/// others are under it by how far they are zoomed, finest uppermost, so where one runs out the next
/// finest shows through. At rest the one on top covers the whole screen, because a viewport never
/// shows past the picture's edge, so what a click picks is always what it drew (§7.1).</para>
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

    private readonly List<ExploreLayer> _layers = [];

    private readonly PaintBuffers _buffers;

    /// <summary>A count of the drawings shown, so the least recently shown is the first to go.</summary>
    private long _showings;

    /// <summary>A count of the pictures, so a drawing of one that has changed is never shown again.</summary>
    private int _picture;

    /// <summary>Where the screen was last placed, so a drawing that starts landing is placed with the rest.</summary>
    private (MapViewport Shown, double Width, double Height) _placement = (MapViewport.Whole, 0, 0);

    private ExploreLayer? _current;

    public ExploreLayers(PaintBuffers buffers)
    {
        _buffers = buffers;
        _panel.RenderTransform = _carried;
    }

    public UIElement Element => _panel;

    /// <summary>
    /// Whether the whole picture is missing under a zoomed drawing on top, and is not landing either.
    /// See <see cref="ExploreMap"/>, which asks for it once the drawing on top is on screen.
    /// </summary>
    public bool LacksWhole =>
        _current is { Viewport.IsWhole: false }
        && !_layers.Exists(layer => layer.Picture == _picture && layer.Viewport.IsWhole && layer.IsShown);

    /// <summary>
    /// Show the drawing already made of <paramref name="viewport"/> of the picture now, on top, and
    /// say what it is. Null where there is none, and a redraw has to make it.
    /// </summary>
    public ExploreSurface? Show(MapViewport viewport)
    {
        if (Kept(viewport) is not { } layer)
        {
            return null;
        }

        Promote(layer);

        return layer.Drawing;
    }

    /// <summary>
    /// Where a redraw of the picture now lands: over everything, as the drawing the map works from
    /// once it has landed, where <paramref name="onTop"/>; and otherwise in its place among the rest by
    /// how far it is zoomed, which is how the whole picture is painted under a zoomed one.
    /// <paramref name="arrived"/> is told once all of it has landed.
    /// </summary>
    public ICanvasRedrawTarget Arrival(bool onTop, Action<ExploreSurface> arrived) =>
        new Landing(this, onTop, arrived);

    /// <summary>
    /// Stop showing any drawing again, for a picture that has changed: another tree, colours, size or
    /// theme. The drawings stay on screen until the next one has landed over them, and their bitmaps
    /// are painted into after that.
    /// </summary>
    public void Forget() => _picture++;

    /// <summary>
    /// Drop every drawing, for a map with nothing to show or nobody to show it to. The last bitmap
    /// goes back to the buffers for the next drawing. Anything landing has been withdrawn first.
    /// </summary>
    public void Clear()
    {
        Forget();

        foreach (var layer in _layers)
        {
            Hide(layer);
        }

        _current = null;
        Release(0);
    }

    /// <summary>
    /// Keep only the drawing on top, for a map leaving the screen. It is what the map shows when it
    /// comes back, and the others are memory nothing shows until the picture moves again, which asks
    /// for them afresh. Anything landing has been withdrawn first.
    /// </summary>
    public void Trim()
    {
        foreach (var layer in _layers)
        {
            if (layer != _current)
            {
                Hide(layer);
            }
        }

        Release(0);
    }

    /// <summary>
    /// Put each drawing where its part of the picture is on a screen <paramref name="width"/> by
    /// <paramref name="height"/> showing <paramref name="shown"/>, and the one being worked from on top.
    /// </summary>
    public void Place(MapViewport shown, double width, double height)
    {
        _placement = (shown, width, height);

        foreach (var layer in _layers)
        {
            if (!layer.IsShown)
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
    /// Make <paramref name="layer"/> the one the map works from, and let go of the bitmaps it no longer
    /// needs to keep.
    /// </summary>
    private void Promote(ExploreLayer layer)
    {
        layer.LastShown = ++_showings;
        _current = layer;

        // A zoomed drawing keeps bitmaps no drawing is using yet, up to as many as the zoom keeps:
        // the whole picture is painted under it next, and the zoom's next stop after that. Handed
        // back and asked for again, all but one would go to the collector and be allocated anew.
        Release(layer.Viewport.IsWhole ? 0 : 1 + ZoomedKept);
    }

    /// <summary>
    /// Where <paramref name="layer"/> is stacked: a drawing landing to replace the one on top above
    /// everything, the one on top above the rest, and each other above every one coarser than it, or
    /// as fine and shown less recently. A rank rather than the zoom itself, because two drawings a
    /// drag apart share a zoom and would share a place, and which of them showed through would then
    /// be the panel's choice.
    /// </summary>
    private int Rank(ExploreLayer layer)
    {
        var rank = 0;

        foreach (var other in _layers)
        {
            if (other != layer && other.IsShown && Above(layer, other))
            {
                rank++;
            }
        }

        return rank;
    }

    /// <summary>Whether <paramref name="layer"/> is stacked above <paramref name="other"/>. See <see cref="Rank"/>.</summary>
    private bool Above(ExploreLayer layer, ExploreLayer other)
    {
        var layerReplaces = layer is { Landing: not null, LandsOnTop: true };
        var otherReplaces = other is { Landing: not null, LandsOnTop: true };

        if (layerReplaces || otherReplaces)
        {
            return layerReplaces && !otherReplaces;
        }

        if (layer == _current || other == _current)
        {
            return layer == _current;
        }

        return other.Viewport.Zoom < layer.Viewport.Zoom
            || (other.Viewport.Zoom == layer.Viewport.Zoom && other.LastShown < layer.LastShown);
    }

    /// <summary>The drawing already made of <paramref name="viewport"/> of the picture now, or null.</summary>
    private ExploreLayer? Kept(MapViewport viewport) =>
        _layers.Find(layer => layer.Drawing is not null && layer.Picture == _picture && layer.Viewport == viewport);

    /// <summary>
    /// A bitmap for a drawing of <paramref name="viewport"/> to land in: one no drawing is using; or
    /// one of a picture that has changed; or where every one is in use, the zoomed one least recently
    /// shown. Never the whole picture of the picture now, nor the one on top, which goes on showing
    /// until this one has landed over it.
    /// </summary>
    private ExploreLayer Take(int width, int height, MapViewport viewport)
    {
        var layer = Free(width, height)
            ?? _layers
                .Where(kept => kept.Drawing is not null && kept.Picture != _picture && kept != _current)
                .MinBy(kept => kept.LastShown);

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
            layer = new ExploreLayer();
            _layers.Add(layer);
            _panel.Children.Add(layer.Image);
        }

        // Reused while the size holds. A scan redraws a map every three quarters of a second, and at
        // 3840 by 2160 each bitmap is 33 MB, so a new one per drawing would be megabytes of garbage a
        // second for a surface whose size only changes with the window's (G5).
        if (layer.Bitmap is not { } bitmap || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
        {
            bitmap = _buffers.BitmapFor(width, height);
            layer.Bitmap = bitmap;
            layer.Image.Source = bitmap;
        }

        layer.Drawing = null;

        return layer;
    }

    /// <summary>A bitmap nothing is using, of this size where there is one.</summary>
    private ExploreLayer? Free(int width, int height)
    {
        ExploreLayer? other = null;

        foreach (var layer in _layers)
        {
            if (layer.IsShown)
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

    private static void Hide(ExploreLayer layer)
    {
        layer.Drawing = null;
        layer.Landing = null;
        layer.Image.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Take out layers nothing is using until no more than <paramref name="keep"/> are left in all,
    /// and hand each one's bitmap back to the buffers for the next drawing.
    /// </summary>
    private void Release(int keep)
    {
        for (var i = _layers.Count - 1; i >= 0 && _layers.Count > keep; i--)
        {
            var layer = _layers[i];

            if (layer.IsShown)
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

    /// <summary>One redraw landing in one of these layers. See <see cref="ICanvasRedrawTarget"/>.</summary>
    private sealed class Landing(ExploreLayers layers, bool onTop, Action<ExploreSurface> arrived)
        : ICanvasRedrawTarget
    {
        private ExploreLayer? _layer;

        public void Begin(CanvasRedraw redraw)
        {
            var drawing = redraw.Surface!;
            var viewport = drawing.Viewport ?? MapViewport.Whole;
            var layer = layers.Take(drawing.Width, drawing.Height, viewport);

            layer.Clear();
            layer.Landing = redraw;
            layer.Viewport = viewport;
            layer.LandsOnTop = onTop;
            layer.Picture = layers._picture;
            layer.Image.Visibility = Visibility.Visible;

            _layer = layer;

            var (shown, width, height) = layers._placement;

            layers.Place(shown, width, height);
        }

        public void Land(CanvasRedraw redraw, IReadOnlyList<CanvasRegion> regions) =>
            _layer!.Land(redraw.Pixels, regions);

        public void Arrive(CanvasRedraw redraw)
        {
            var layer = _layer!;
            var drawing = redraw.Surface!;

            layer.Landing = null;
            layer.Drawing = drawing;
            layer.LastShown = ++layers._showings;

            if (onTop)
            {
                // The drawings of the picture this one replaces have been showing round it while it
                // landed. Covered now, they go.
                foreach (var other in layers._layers)
                {
                    if (other.Picture != layer.Picture)
                    {
                        Hide(other);
                    }
                }

                layers.Promote(layer);
            }

            arrived(drawing);
        }

        public void Withdraw(CanvasRedraw redraw) => Hide(_layer!);
    }
}
