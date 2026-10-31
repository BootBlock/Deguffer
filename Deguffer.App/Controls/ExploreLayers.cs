using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
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
/// <para>The drawing the map works from is on top, under only a drawing landing to replace it, and the
/// others under it by how far they are zoomed: see <see cref="KeptDrawings"/>, which also decides which
/// surface the next drawing lands on and which drawings a new one covers. At rest the one on top covers
/// the whole screen, because a viewport never shows past the picture's edge, so what a click picks is
/// always what it drew (§7.1).</para>
///
/// <para>While the camera moves, the tiles of the picture (<see cref="ExploreTiles"/>) are stacked
/// round the drawing on top: those drawn more finely than it over it, so a zoom in is sharp, and the
/// rest under it, where they show only past its edges. They go once the drawing the camera comes to
/// rest at has arrived, and with the picture they are tiles of. These own the tiles, and the map asks
/// for them only through these.</para>
///
/// <para>Separate from <see cref="ExploreMap"/>, which decides what is drawn and what the pointer is
/// over; this holds surfaces and places them, and knows nothing of trees (G1).</para>
///
/// <para>Over 250 lines because keeping the drawings, landing one, crossfading it in, stacking them
/// with the tiles and carrying them while a folder opens all act on the one set of layers and its
/// order, and each would otherwise reach into the others' state. The rules they follow are in
/// <see cref="KeptDrawings"/>, a landing's own calls in <see cref="ExploreLanding"/>, and the
/// overview's copy in <see cref="ExploreMiniature"/>.</para>
/// </summary>
internal sealed class ExploreLayers
{
    private readonly Compositor _compositor;

    private readonly MapGraphics _graphics;

    /// <summary>Where all of it is carried to, in the screen's terms, while an opened folder grows over it.</summary>
    private readonly ContainerVisual _carried;

    /// <summary>The visual the camera moves: where the whole picture is on the screen.</summary>
    private readonly ContainerVisual _cameraVisual;

    /// <summary>The map's camera, which <see cref="_cameraVisual"/> follows while these are the picture the map works from.</summary>
    private readonly MapCamera _camera;

    private readonly List<ExploreLayer> _layers = [];

    /// <summary>The overview's copy of the latest whole drawing: see <see cref="Overview"/>.</summary>
    private readonly ExploreMiniature _miniature;

    /// <summary>The tiles of the picture now, shown round the drawing on top while the camera moves.</summary>
    private readonly ExploreTiles _tiles;

    /// <summary>A count of the drawings shown, so the least recently shown is the first to go.</summary>
    private long _showings;

    /// <summary>A count of the pictures, so a drawing of one that has changed is never shown again.</summary>
    private int _picture;

    /// <summary>The size of the screen the picture is laid out over, so a drawing that starts landing is placed with the rest.</summary>
    private (double Width, double Height) _size;

    /// <summary>What the picture is measured from, so a drawing that starts landing is placed with the rest.</summary>
    private MapOrigin _origin;

    private ExploreLayer? _current;

    /// <summary>The layer the pointer's light is on, or null: see <see cref="Light"/>.</summary>
    private ExploreLayer? _lit;

    /// <summary>
    /// How many drawings are fading in over the pictures before them. While one is, those pictures are
    /// what shows through it, so none of their surfaces is taken for another drawing.
    /// </summary>
    private int _fading;

    /// <summary>
    /// How many times the crossfades under way were brought to their end at once (<see cref="LandFades"/>),
    /// so a fade's own end, which comes later, does not count it a second time.
    /// </summary>
    private int _fadesLanded;

    /// <param name="motion">Whether the tiles crossfade in. See <see cref="ExploreTiles"/>.</param>
    /// <param name="owner">The map's thread, where the tiles drawn off it are put on screen.</param>
    public ExploreLayers(
        Compositor compositor,
        MapGraphics graphics,
        MapCamera camera,
        IMotionPolicy motion,
        SynchronizationContext owner)
    {
        _compositor = compositor;
        _graphics = graphics;
        _camera = camera;
        _miniature = new ExploreMiniature(graphics, () => OverviewChanged?.Invoke(this, EventArgs.Empty));
        _tiles = new ExploreTiles(compositor, graphics, motion, owner);
        _tiles.AnswersChanged += (_, _) => TileAnswersChanged?.Invoke(this, EventArgs.Empty);

        _cameraVisual = compositor.CreateContainerVisual();
        _carried = compositor.CreateContainerVisual();
        _carried.Children.InsertAtTop(_cameraVisual);
        _cameraVisual.Children.InsertAtBottom(_tiles.Under);
        _cameraVisual.Children.InsertAtTop(_tiles.Over);
    }

    /// <summary>The top of these drawings in the composition tree.</summary>
    public Visual Root => _carried;

    /// <summary>
    /// A small copy of the whole picture, for the overview a zoomed map shows in a corner, or null
    /// while none has arrived of the place on screen. See <see cref="ExploreMiniature"/> for why it is a
    /// copy, and kept here.
    /// </summary>
    public ICompositionSurface? Overview => _miniature.Surface;

    /// <summary><see cref="Overview"/> has a new answer.</summary>
    public event EventHandler? OverviewChanged;

    /// <summary>What a tile on screen answers for the pointer has changed. See <see cref="ExploreTiles.AnswersChanged"/>.</summary>
    public event EventHandler? TileAnswersChanged;

    /// <summary>
    /// Whether a drawing is still fading in over the ones before it: the screen shows two drawings at
    /// once, and a point on it is in neither of them alone (§7.1).
    /// </summary>
    public bool IsFading => _fading > 0;

    /// <summary>Raised when the last crossfade under way has run its course.</summary>
    public event EventHandler? Faded;

    /// <summary>
    /// Whether the whole picture is missing under a zoomed drawing on top, and is not landing either.
    /// See <see cref="ExploreMap"/>, which asks for it once the drawing on top is on screen.
    /// </summary>
    public bool LacksWhole => KeptDrawings.LacksWhole(_layers, _current, _picture);

    /// <summary>
    /// The drawing the map works from, where it is of the picture now and is a treemap that can be cut
    /// into tiles; null otherwise. A drawing of a picture that has changed would cut tiles of the old one.
    /// </summary>
    private TiledSurface? Tiled =>
        _current is { Drawing: TiledSurface { Viewport: not null } drawing } current && current.Picture == _picture
            ? drawing
            : null;

    /// <summary>
    /// Show the drawing already made of <paramref name="viewport"/> of the picture now, on top, and
    /// say what it is. Null where there is none, and a redraw has to make it.
    /// </summary>
    public ExploreSurface? Show(MapViewport viewport)
    {
        if (KeptDrawings.Kept(_layers, viewport, _picture) is not { } layer)
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
    public ICompositionSurface? SurfaceOf(ExploreSurface drawing) => Holding(drawing)?.Surface;

    /// <summary>
    /// Put the pointer's light at <paramref name="light"/> on <paramref name="drawing"/>, and take it off
    /// whichever drawing had it, or take it off altogether with nulls. One drawing has it at most, so a
    /// drawing kept and shown again later comes back without a light it no longer has a pointer for.
    /// </summary>
    public void Light(ExploreSurface? drawing, PointerLight? light)
    {
        var layer = drawing is null ? null : Holding(drawing);

        if (layer != _lit)
        {
            _lit?.Light(null);
            _lit = layer;
        }

        layer?.Light(light);
    }

    /// <summary>
    /// Move with the map's camera, for the drawings of the picture the map works from. The compositor
    /// follows it: nothing here is written as it moves.
    /// </summary>
    public void Follow() => _camera.Follow(_cameraVisual);

    /// <summary>
    /// Stay where <paramref name="camera"/> put the picture, for the drawings a folder is opening out
    /// of: the map's camera goes on to the new picture, and these stay where the folder was opened.
    /// </summary>
    public void Freeze(MapTransform camera) => MapCamera.Freeze(_cameraVisual, camera);

    /// <summary>
    /// Show the tiles of the picture the screen shows, <paramref name="shown"/> on the way to
    /// <paramref name="target"/>, and draw those missing from the point of the picture
    /// <paramref name="focusX"/>, <paramref name="focusY"/> outwards. Nothing where the drawing the map
    /// works from cannot be cut into tiles. See <see cref="ExploreTiles.Want"/>.
    /// </summary>
    public void WantTiles(MapViewport shown, MapViewport target, double focusX, double focusY, TileColour ground)
    {
        if (Tiled is { } picture)
        {
            _tiles.Want(picture, shown, target, focusX, focusY, ground);
        }
    }

    /// <summary>The camera has come to rest: see <see cref="ExploreTiles.Rest"/>.</summary>
    public void RestTiles() => _tiles.Rest();

    /// <summary>The drawing on top covers the screen at rest: see <see cref="ExploreTiles.Hide"/>.</summary>
    public void HideTiles() => _tiles.Hide();

    /// <summary>Nobody sees a move of the map: see <see cref="ExploreTiles.Release"/>.</summary>
    public void ReleaseTiles() => _tiles.Release();

    /// <summary>The tile on screen at the point of the picture <paramref name="x"/>, <paramref name="y"/>: see <see cref="ExploreTiles.TileOver"/>.</summary>
    public MapTile? TileOver(double x, double y) => _tiles.TileOver(x, y);

    /// <summary>
    /// Where a redraw of the picture now lands to replace the drawing the map works from: over
    /// everything, and the drawing worked from once it has landed, when <paramref name="arrived"/> is
    /// told.
    /// </summary>
    /// <param name="fade">
    /// How it comes in over the one it replaces. Unseen while it lands and faded in once all of it has,
    /// for the same picture in a new look, so the old look turns into the new one rather than giving way
    /// to it a region at a time. <see cref="Motion.Instant"/> lands it as it is painted.
    /// </param>
    public ICanvasRedrawTarget Replacement(Action<ExploreSurface> arrived, Motion fade) =>
        new ExploreLanding(this, onTop: true, arrived, fade);

    /// <summary>
    /// Where a redraw of the whole picture lands under a zoomed drawing on top: in its place among the
    /// rest by how far it is zoomed, as it is painted, so a move has something to show wherever it goes.
    /// See <see cref="LacksWhole"/>.
    /// </summary>
    public ICanvasRedrawTarget Underlay() =>
        new ExploreLanding(this, onTop: false, static _ => { }, Motion.Instant);

    /// <summary>
    /// Start a redraw of <paramref name="drawing"/> landing, on a surface of its own, placed where its part
    /// of the picture lies and stacked with the rest. See <see cref="ExploreLanding"/>.
    /// </summary>
    /// <param name="onTop">Whether it replaces the drawing the map works from.</param>
    /// <param name="fade">How it comes in, where it replaces it. See <see cref="Replacement"/>.</param>
    public ExploreLayer StartLanding(ExploreSurface drawing, bool onTop, Motion fade)
    {
        var viewport = drawing.Viewport ?? MapViewport.Whole;
        var layer = Take(drawing.Width, drawing.Height, viewport);

        layer.Begin(drawing.Width, drawing.Height, viewport, onTop, _picture, unseen: !fade.IsInstant);
        layer.Fit(_size.Width, _size.Height, _origin);

        Restack();

        return layer;
    }

    /// <summary>
    /// All of <paramref name="drawing"/> has landed on <paramref name="layer"/>. A whole drawing is copied
    /// for the overview, and one that replaces the drawing worked from takes its place, faded in over it
    /// or covering it at once. See <see cref="ExploreLanding"/>.
    /// </summary>
    /// <param name="pixels">The drawing as painted on the CPU, or null for one shaded on the GPU.</param>
    /// <param name="onTop">Whether it replaces the drawing the map works from.</param>
    /// <param name="fade">How it comes in, where it replaces it. See <see cref="Replacement"/>.</param>
    public void FinishLanding(ExploreLayer layer, ExploreSurface drawing, byte[]? pixels, bool onTop, Motion fade)
    {
        layer.Arrive(drawing, ++_showings);

        if (layer.Viewport.IsWhole)
        {
            _miniature.Copy(pixels, drawing.Width, drawing.Height, _size.Width, _size.Height);
        }

        if (onTop)
        {
            if (fade.IsInstant)
            {
                // The drawings of the pictures this one replaces have been showing round it while it
                // landed. Covered now, they go.
                HideCoveredBy(layer, spared: null);
            }
            else
            {
                FadeIn(layer, fade.Duration);
            }

            Promote(layer);
        }

        Restack();
    }

    /// <summary>
    /// Bring every crossfade under way to its end at once, for an input that needs the screen settled
    /// first: the drawing on top covers it, and the ones it was fading in over go.
    /// </summary>
    public void LandFades()
    {
        if (_fading == 0 || _current is not { } current)
        {
            return;
        }

        _fading = 0;
        _fadesLanded++;

        current.EndFade();
        HideCoveredBy(current, spared: null);
    }

    /// <summary>
    /// Stop showing any drawing again, for a picture that has changed: another tree, colours, size or
    /// theme. The drawings stay on screen until the next one has landed over them, and their surfaces
    /// are written into after that.
    /// </summary>
    public void Forget()
    {
        _picture++;
        _tiles.Forget();
    }

    /// <summary>
    /// The picture is of another place now: another tree, folder or view. The overview of the old
    /// one goes at once, rather than standing for the new one until its whole drawing arrives.
    /// </summary>
    public void ForgetOverview() => _miniature.Forget();

    /// <summary>
    /// Drop every drawing, and the memory its surface held, for a map with nothing to show or nobody
    /// to show it to. Anything landing has been withdrawn first.
    /// </summary>
    public void Clear()
    {
        Forget();

        foreach (var layer in _layers)
        {
            layer.Withdraw();
        }

        _current = null;
        _lit = null;
        Release();
        ForgetOverview();
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

        _tiles.Release();

        _current = null;
        _lit = null;
        _miniature.Renew();
    }

    /// <summary>
    /// Keep only the drawing on top, for a map leaving the screen. It is what the map shows when it
    /// comes back, and the others and the tiles are memory nothing shows until the picture moves again,
    /// which asks for them afresh. Anything landing has been withdrawn first.
    /// </summary>
    public void Trim()
    {
        foreach (var layer in _layers)
        {
            if (layer != _current)
            {
                layer.Withdraw();
            }
        }

        Release();
        _tiles.Release();
    }

    /// <summary>
    /// Put each drawing where its part of the picture lies, in a picture laid out over a screen
    /// <paramref name="width"/> by <paramref name="height"/>, measured from <paramref name="origin"/>.
    /// Only for a new size or a new origin: the camera moves the picture, and nothing here changes as
    /// it does.
    /// </summary>
    public void Fit(double width, double height, MapOrigin origin)
    {
        _size = (width, height);
        _origin = origin;

        foreach (var layer in _layers)
        {
            if (layer.IsShown)
            {
                layer.Fit(width, height, origin);
            }
        }

        _tiles.Fit(width, height, origin);
    }

    /// <summary>
    /// Carry all of it so the screen lands on <paramref name="screen"/>, at
    /// <paramref name="opacity"/>, while a folder that was opened grows over the picture or out of it.
    /// </summary>
    /// <param name="width">
    /// The control's width now. Not the size <see cref="Fit"/> stored: the set a change of folder is
    /// leaving is not fitted again when the control is resized while the change flies.
    /// </param>
    /// <param name="height">The control's height now, as <paramref name="width"/>.</param>
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
        layer.Show(++_showings);
        _current = layer;
        _tiles.Stack(layer.Viewport);

        Release();
    }

    /// <summary>
    /// Stack the drawings by <see cref="KeptDrawings.Stacked"/>, lowest first, with the tiles round the
    /// one on top: the finer under a drawing landing to replace it and over it, the coarser under it.
    /// Only what is shown, and which drawing is worked from, changes the order, so this runs when they
    /// change and never as the picture moves.
    /// </summary>
    private void Restack()
    {
        foreach (var layer in KeptDrawings.Stacked(_layers, _current))
        {
            _cameraVisual.Children.Remove(layer.Sprite);
            _cameraVisual.Children.InsertAtTop(layer.Sprite);
        }

        _cameraVisual.Children.Remove(_tiles.Under);
        _cameraVisual.Children.Remove(_tiles.Over);

        if (_current is { IsShown: true } current)
        {
            _cameraVisual.Children.InsertBelow(_tiles.Under, current.Sprite);
            _cameraVisual.Children.InsertAbove(_tiles.Over, current.Sprite);
        }
        else
        {
            _cameraVisual.Children.InsertAtBottom(_tiles.Over);
            _cameraVisual.Children.InsertAtBottom(_tiles.Under);
        }
    }

    /// <summary>The layer whose whole drawing is <paramref name="drawing"/>, or null. A loop, as the pointer asks it at every move.</summary>
    private ExploreLayer? Holding(ExploreSurface drawing)
    {
        foreach (var layer in _layers)
        {
            if (ReferenceEquals(layer.Drawing, drawing))
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>
    /// A surface for a drawing <paramref name="width"/> by <paramref name="height"/> of
    /// <paramref name="viewport"/> to land on: the one <see cref="KeptDrawings.Reusable"/> picks, or a
    /// new one where it picks none.
    /// </summary>
    private ExploreLayer Take(int width, int height, MapViewport viewport)
    {
        // Emptied and resized when it begins, rather than made again: a scan redraws a map as often as
        // every three quarters of a second, and the surface's size only changes with the window's (G5).
        if (KeptDrawings.Reusable(_layers, width, height, viewport, _picture, _current, _fading > 0) is { } reused)
        {
            return reused;
        }

        var layer = new ExploreLayer(_compositor, _graphics);
        _layers.Add(layer);
        _cameraVisual.Children.InsertAtTop(layer.Sprite);

        return layer;
    }

    /// <summary>
    /// Stop showing every drawing <paramref name="cover"/> covers, but <paramref name="spared"/>. See
    /// <see cref="KeptDrawings.IsCoveredBy"/>.
    /// </summary>
    private void HideCoveredBy(ExploreLayer cover, ExploreLayer? spared)
    {
        foreach (var other in _layers)
        {
            if (KeptDrawings.IsCoveredBy(other, cover, spared))
            {
                other.Withdraw();
            }
        }
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

    /// <summary>
    /// Fade <paramref name="layer"/> in over the drawings of the pictures before it, which go once it
    /// covers them. Only those older than it: a picture handed over during the fade has drawings of its
    /// own on the way, which are not this fade's to take.
    /// </summary>
    private void FadeIn(ExploreLayer layer, TimeSpan duration)
    {
        var fading = layer.Sprite.FadeIn(duration);
        _fading++;

        var landed = _fadesLanded;

        fading.Completed += (_, _) =>
        {
            // The layer can have been taken for a later picture since the fade began, so the drawing
            // worked from now is spared rather than covered by a drawing that has not arrived.
            HideCoveredBy(layer, spared: _current);

            // Brought to its end already, and counted then.
            if (landed == _fadesLanded && --_fading == 0)
            {
                Faded?.Invoke(this, EventArgs.Empty);
            }
        };
    }
}
