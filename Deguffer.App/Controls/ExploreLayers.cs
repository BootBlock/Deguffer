using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// The drawings a map keeps of the one picture it shows: the whole of it, and the last parts of it a
/// zoom or a drag stopped at, each a composition surface placed where its part of the picture lies.
///
/// <para>Each drawing is placed in the whole picture once, and the whole picture on the screen by a
/// camera: a visual above them all that follows the map's camera by an expression. So a move is a new
/// camera and nothing else. No drawing is placed again, and the compositor carries all of them.</para>
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
/// <para>A new drawing lands a region at a time (see <see cref="CanvasRedraw"/>), onto a surface of
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
/// over; this holds surfaces and places them, and knows nothing of trees (G1).</para>
/// </summary>
internal sealed class ExploreLayers
{
    /// <summary>
    /// How many zoomed drawings to keep besides the whole picture, the one on screen included.
    ///
    /// <para>Each is a surface the size of the canvas, which is 33 MB at 3840 by 2160. Two keeps the
    /// last place a zoom or a drag stopped at under the one it stops at next: finer than the whole
    /// picture where the reader goes back over it, and shown again outright where they return to
    /// exactly that place.</para>
    /// </summary>
    private const int ZoomedKept = 2;

    private readonly Compositor _compositor;

    private readonly MapGraphics _graphics;

    /// <summary>Where all of it is carried to, in the screen's terms, while an opened folder grows over it.</summary>
    private readonly ContainerVisual _carried;

    /// <summary>The camera: where the whole picture is on the screen.</summary>
    private readonly ContainerVisual _camera;

    private readonly MapCamera _mapCamera;

    private readonly List<ExploreLayer> _layers = [];

    /// <summary>A count of the drawings shown, so the least recently shown is the first to go.</summary>
    private long _showings;

    /// <summary>A count of the pictures, so a drawing of one that has changed is never shown again.</summary>
    private int _picture;

    /// <summary>The size of the screen the picture is laid out over, so a drawing that starts landing is placed with the rest.</summary>
    private (double Width, double Height) _size;

    private ExploreLayer? _current;

    public ExploreLayers(Compositor compositor, MapGraphics graphics, MapCamera camera)
    {
        _compositor = compositor;
        _graphics = graphics;
        _mapCamera = camera;

        _camera = compositor.CreateContainerVisual();
        _carried = compositor.CreateContainerVisual();
        _carried.Children.InsertAtTop(_camera);
    }

    /// <summary>The top of these drawings in the composition tree.</summary>
    public Visual Root => _carried;

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
        Restack();

        return layer.Drawing;
    }

    /// <summary>
    /// The surface <paramref name="drawing"/> is painted on, once all of it is there, or null where
    /// none of these holds it whole.
    /// </summary>
    public ICompositionSurface? SurfaceOf(ExploreSurface drawing) =>
        _layers.Find(layer => ReferenceEquals(layer.Drawing, drawing))?.Surface;

    /// <summary>
    /// Move with the map's camera, for the drawings of the picture the map works from. The compositor
    /// follows it: nothing here is written as it moves.
    /// </summary>
    public void Follow() => _mapCamera.Follow(_camera);

    /// <summary>
    /// Stay where <paramref name="camera"/> put the picture, for the drawings a folder is opening out
    /// of: the map's camera goes on to the new picture, and these stay where the folder was opened.
    /// </summary>
    public void Freeze(MapTransform camera) => MapCamera.Freeze(_camera, camera);

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
    /// theme. The drawings stay on screen until the next one has landed over them, and their surfaces
    /// are written into after that.
    /// </summary>
    public void Forget() => _picture++;

    /// <summary>
    /// Drop every drawing, and the memory its surface held, for a map with nothing to show or nobody
    /// to show it to. Anything landing has been withdrawn first.
    /// </summary>
    public void Clear()
    {
        Forget();

        foreach (var layer in _layers)
        {
            Hide(layer);
        }

        _current = null;
        Release();
    }

    /// <summary>
    /// Drop every drawing, and give every layer a new surface, for a device that has replaced the
    /// one they were written through. Anything landing has been withdrawn first.
    /// </summary>
    public void Renew()
    {
        Forget();

        foreach (var layer in _layers)
        {
            layer.Renew();
        }

        _current = null;
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

        Release();
    }

    /// <summary>
    /// Put each drawing where its part of the picture lies, in a picture laid out over a screen
    /// <paramref name="width"/> by <paramref name="height"/>. Only for a new size: the camera moves the
    /// picture, and nothing here changes as it does.
    /// </summary>
    public void Fit(double width, double height)
    {
        _size = (width, height);

        foreach (var layer in _layers)
        {
            if (layer.IsShown)
            {
                layer.Fit(width, height);
            }
        }
    }

    /// <summary>
    /// Carry all of it so the screen lands on <paramref name="screen"/>, at
    /// <paramref name="opacity"/>, while a folder that was opened grows over the picture or out of it.
    /// </summary>
    public void Carry(MapFrame screen, double opacity, double width, double height)
    {
        _carried.Scale = new Vector3((float)screen.Width, (float)screen.Height, 1);
        _carried.Offset = new Vector3((float)(screen.X * width), (float)(screen.Y * height), 0);
        _carried.Opacity = (float)opacity;
    }

    /// <summary>
    /// Make <paramref name="layer"/> the one the map works from, and let go of the surfaces it no
    /// longer needs to keep.
    /// </summary>
    private void Promote(ExploreLayer layer)
    {
        layer.LastShown = ++_showings;
        _current = layer;

        Release();
    }

    /// <summary>
    /// Stack the drawings by <see cref="Rank"/>, lowest first. Only what is shown, and which drawing
    /// is worked from, changes the order, so this runs when they change and never as the picture moves.
    /// </summary>
    private void Restack()
    {
        foreach (var layer in _layers.Where(layer => layer.IsShown).OrderBy(Rank).ToList())
        {
            _camera.Children.Remove(layer.Sprite);
            _camera.Children.InsertAtTop(layer.Sprite);
        }
    }

    /// <summary>
    /// Where <paramref name="layer"/> is stacked: a drawing landing to replace the one on top above
    /// everything, the one on top above the rest, and each other above every one coarser than it, or
    /// as fine and shown less recently. A rank rather than the zoom itself, because two drawings a
    /// drag apart share a zoom and would share a place, and which of them showed through would then
    /// be the compositor's choice.
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
    /// A surface for a drawing of <paramref name="viewport"/> to land on: one no drawing is using; or
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
            layer = new ExploreLayer(_compositor, _graphics);
            _layers.Add(layer);
            _camera.Children.InsertAtTop(layer.Sprite);
        }

        // Emptied and resized rather than made again: a scan redraws a map every three quarters of a
        // second, and the surface's size only changes with the window's (G5).
        layer.Drawing = null;
        layer.Prepare(width, height);

        return layer;
    }

    /// <summary>A surface nothing is using, of this size where there is one.</summary>
    private ExploreLayer? Free(int width, int height)
    {
        ExploreLayer? other = null;

        foreach (var layer in _layers)
        {
            if (layer.IsShown)
            {
                continue;
            }

            if (layer.Size.Width == width && layer.Size.Height == height)
            {
                return layer;
            }

            other ??= layer;
        }

        return other;
    }

    /// <summary>
    /// Stop showing <paramref name="layer"/>. Its surface keeps what it holds until it is released or
    /// taken for the next drawing, which empties it first.
    /// </summary>
    private static void Hide(ExploreLayer layer)
    {
        layer.Drawing = null;
        layer.Landing = null;
        layer.Sprite.IsVisible = false;
    }

    /// <summary>
    /// Empty the surface of every layer nothing is using, so it holds no memory. The layers stay, to
    /// be written through again (G5). An empty layer costs a visual and nothing more, and there are
    /// never more of them than the drawings kept and one landing.
    /// </summary>
    private void Release()
    {
        foreach (var layer in _layers)
        {
            if (!layer.IsShown)
            {
                layer.Empty();
            }
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

            layer.Landing = redraw;
            layer.Viewport = viewport;
            layer.LandsOnTop = onTop;
            layer.Picture = layers._picture;
            layer.Fit(layers._size.Width, layers._size.Height);
            layer.Sprite.IsVisible = true;

            _layer = layer;

            layers.Restack();
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

            layers.Restack();
            arrived(drawing);
        }

        public void Withdraw(CanvasRedraw redraw) => Hide(_layer!);
    }
}
