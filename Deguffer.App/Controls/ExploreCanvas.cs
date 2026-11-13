using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Deguffer.App.Controls;

/// <summary>
/// The redraw pipeline of an <see cref="ExploreMap"/>: each drawing of the picture laid out and painted
/// off the UI thread at the size and the display scale the map is at, and put on screen a region at a
/// time (<see cref="CanvasRedraws"/>), one redraw at a time.
///
/// <para>It decides nothing about what the picture is, or what the map does with a drawing: the map hands
/// it an <see cref="ExplorePainting"/> to draw, and works from what arrives (its own <c>Present</c>). What
/// it decides is how a drawing is made: whether the map can be drawn at all, whether a drawing still fits
/// it, when a run of size changes has settled, which redraw is landing and which is owed, and the whole
/// picture painted under a zoomed drawing and the tiles a moving camera shows.</para>
///
/// <para>Past 250 lines for the reasoning behind each of those, which is most of its length.</para>
/// </summary>
internal sealed class ExploreCanvas
{
    /// <summary>
    /// How long a run of size changes has to stop for before the map is drawn again.
    ///
    /// <para>Dragging a window edge raises <see cref="FrameworkElement.SizeChanged"/> tens of times
    /// a second, and each one is a whole layout of the tree, a fresh canvas, and a pass over every
    /// pixel of it — at 3840 by 2160 that is eight million pixels shaded several times over. Drawing
    /// each of those in turn is not merely repeated work: it is work for a size that was superseded
    /// before the paint finished, so the window falls further behind the pointer the longer the drag
    /// goes on.</para>
    ///
    /// <para>So the size changes are coalesced and only the size the user settles on is drawn. In
    /// between, each drawing kept stretches over the new bounds, which is the right picture at the
    /// wrong scale and is on screen with no work at all.</para>
    /// </summary>
    private static readonly TimeSpan ResizeSettleTime = TimeSpan.FromMilliseconds(120);

    /// <summary>The map drawn: its size, its display scale, and whether it is on screen.</summary>
    private readonly FrameworkElement _host;

    private readonly MapGraphics _graphics;

    private readonly ExploreZoom _zoom;

    /// <summary>The set of layers the picture worked from is drawn into, which a change of folder swaps.</summary>
    private readonly ExploreFlight _flight;

    /// <summary>Where the pointer is over the map, so the region under it is painted first.</summary>
    private readonly Func<Point?> _pointer;

    /// <summary>What the map does with each drawing that arrives: work from it.</summary>
    private readonly Action<ExploreSurface> _present;

    /// <summary>
    /// What the drawing the map works from was drawn from, asked only once the whole picture is to be
    /// painted under it.
    /// </summary>
    private readonly Func<ExplorePainting> _painting;

    private readonly PaintBuffers _buffers = new();

    /// <summary>
    /// Lays each drawing out and paints it off the UI thread, and puts it on screen a region at a
    /// time. See <see cref="CanvasRedraws"/>.
    /// </summary>
    private readonly CanvasRedraws _redraws;

    private readonly DispatcherQueueTimer _settled;

    /// <summary>
    /// Draw the map again once a run of size changes has stopped. Held here rather than by the timer,
    /// which holds only a weak reference to this canvas, because the map's callback reaches the map.
    /// </summary>
    private readonly Action _onSettled;

    /// <param name="host">The map drawn.</param>
    /// <param name="present">Work from a drawing that has arrived, which is on top of the rest now.</param>
    /// <param name="painting">What the drawing the map works from was drawn from.</param>
    /// <param name="settled">Draw the map again, once a run of size changes has stopped.</param>
    public ExploreCanvas(
        FrameworkElement host,
        MapGraphics graphics,
        ExploreZoom zoom,
        ExploreFlight flight,
        SynchronizationContext owner,
        Func<Point?> pointer,
        Action<ExploreSurface> present,
        Func<ExplorePainting> painting,
        Action settled)
    {
        _host = host;
        _graphics = graphics;
        _zoom = zoom;
        _flight = flight;
        _pointer = pointer;
        _present = present;
        _painting = painting;
        _redraws = new CanvasRedraws(owner);

        _settled = host.DispatcherQueue.CreateTimer();
        _settled.Interval = ResizeSettleTime;
        _settled.IsRepeating = false;
        _onSettled = settled;
        _settled.TickGuarded(this, static canvas => canvas._onSettled());
    }

    /// <summary>The display scale the drawings are painted at, read as each redraw of the picture starts.</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>
    /// The redraw of the picture the map is to work from next, while it lands. Its regions answer for
    /// the points they cover until it arrives, and the drawing the map works from for every other
    /// point, because each is what the screen shows there (§7.1).
    /// </summary>
    public CanvasRedraw? Arriving { get; private set; }

    /// <summary>The redraw on its way, or null where none is.</summary>
    public CanvasRedraw? Pending => _redraws.Pending;

    /// <summary>
    /// Whether a redraw was stopped by the map leaving the screen before it arrived, so the map owes
    /// one when it comes back.
    /// </summary>
    public bool IsOwed { get; private set; }

    /// <summary>Whether a run of size changes is still waiting to stop before the map is drawn again.</summary>
    public bool IsSettling => _settled.IsRunning;

    /// <summary>
    /// Whether the page has collapsed the map, as it does for a view that draws no picture. A redraw
    /// asks only this, not <see cref="IsShowing"/>: a map away from the screen with its page is still
    /// handed pictures, and draws each one, since nothing else redraws it when it comes back.
    /// </summary>
    public bool IsCollapsed => _host.Visibility != Visibility.Visible;

    /// <summary>
    /// Whether the map is on screen: loaded, and not collapsed. A zoom arriving, the whole picture
    /// painted under a zoomed drawing and the tiles of a moving camera are for a map that is seen, and
    /// the map asks again for what it owes as it comes back.
    /// </summary>
    public bool IsShowing => _host.IsLoaded && !IsCollapsed;

    /// <summary>Whether the map has an area for a point on it to be a fraction of.</summary>
    public bool HasSize => _host.ActualWidth > 0 && _host.ActualHeight > 0;

    /// <summary>Whether the map, at its display scale, has a pixel to draw on each way.</summary>
    public bool HasPixels => DevicePixels(_host.ActualWidth) > 0 && DevicePixels(_host.ActualHeight) > 0;

    /// <summary>
    /// Whether <paramref name="drawing"/> is the size a redraw of the map now would be laid out at,
    /// rather than one stretched over the map since it was made.
    /// </summary>
    public bool Fits(ExploreSurface drawing) =>
        drawing.Width == DevicePixels(_host.ActualWidth) && drawing.Height == DevicePixels(_host.ActualHeight);

    /// <summary>
    /// Wait for the run of size changes to stop before drawing the map again. Stopped and started
    /// rather than started, so each size change puts the whole wait back and a drag that is still
    /// moving never reaches the end of one.
    /// </summary>
    public void AwaitResize()
    {
        _settled.Stop();
        _settled.Start();
    }

    /// <summary>Drop a size change still waiting to be drawn, for a map leaving the screen.</summary>
    public void CancelResize() => _settled.Stop();

    /// <summary>
    /// A redraw of the picture is starting: it is more current than a size change still waiting to be
    /// drawn, or a redraw stopped when the page was left.
    /// </summary>
    public void Supersede()
    {
        _settled.Stop();
        IsOwed = false;
    }

    /// <summary>
    /// Read the display scale the drawings of the picture are painted at, as a redraw of it starts.
    /// </summary>
    public void Rescale() => Scale = _host.XamlRoot?.RasterizationScale ?? 1;

    /// <summary>Stop the redraw on its way, if one is.</summary>
    public void Cancel() => _redraws.Cancel();

    /// <summary>
    /// Stop the redraw on its way and drop every drawing kept, for a map nobody can see. The paint
    /// buffer stays, and so do the layers with their surfaces emptied, so drawing again allocates
    /// neither (G5).
    /// </summary>
    public void Drop()
    {
        _redraws.Cancel();
        _flight.Pictures.Clear();
    }

    /// <summary>
    /// <see cref="Drop"/>, and the buffer the drawings were painted through as well: a map with nothing
    /// to show holds no memory for it.
    /// </summary>
    public void Empty()
    {
        Drop();
        _buffers.Release();
    }

    /// <summary>
    /// The map left the screen. A redraw still landing is stopped, and owed when it is back; one
    /// painting the whole picture under a zoomed drawing is not owed, as it is asked for again when the
    /// picture next moves. The paint buffer goes with it: at 4K it is 33 MB.
    /// </summary>
    public void Leave()
    {
        IsOwed |= Arriving is { IsSettled: false };
        _redraws.Cancel();
        _buffers.Release();
    }

    /// <summary>
    /// The drawing kept of <paramref name="viewport"/> of the picture, shown at once in place of any
    /// redraw on its way, or null where none is kept.
    /// </summary>
    public ExploreSurface? Kept(MapViewport viewport)
    {
        if (_flight.Pictures.Show(viewport) is not { } kept)
        {
            return null;
        }

        _redraws.Cancel();

        return kept;
    }

    /// <summary>
    /// Start a redraw of <paramref name="painting"/> at <paramref name="viewport"/>, which lands over
    /// the picture on screen and is handed to the map as it arrives.
    /// </summary>
    /// <param name="fade">How the new drawing comes in over the old. See <see cref="ExploreLayers.Replacement"/>.</param>
    /// <param name="arrival">What the map will ask of the new drawing as it arrives, worked out on the worker that lays it out.</param>
    public void Draw(ExplorePainting painting, MapViewport viewport, Motion fade, DrawingArrival arrival)
    {
        var width = DevicePixels(_host.ActualWidth);
        var height = DevicePixels(_host.ActualHeight);

        Arriving = Start(
            painting,
            Layout(painting, viewport, width, height, arrival),
            width,
            height,
            Focus(viewport, width, height),
            _flight.Pictures.Replacement(_present, fade));
    }

    /// <summary>
    /// Paint the whole picture under <paramref name="drawing"/>, the zoomed drawing the map works
    /// from, where it is not there already, so a move has something to show
    /// wherever it goes. See <see cref="ExploreLayers"/>.
    ///
    /// <para>Never in place of a redraw still landing, which is the picture the reader asked for: the
    /// two would share the one redraw at a time, and this one would stop that one. It is asked for
    /// again once that one has arrived. Not while a resize is settling either: painted at the new size,
    /// it would be thrown away with every other drawing when the settling redraws the picture.</para>
    /// </summary>
    public void Underlay(ExploreSurface drawing)
    {
        if (!IsShowing || IsSettling || _redraws.Pending is not null || !_flight.Pictures.LacksWhole)
        {
            return;
        }

        var painting = _painting();

        Start(
            painting,
            Layout(painting, MapViewport.Whole, drawing.Width, drawing.Height, arrival: null),
            drawing.Width,
            drawing.Height,
            focus: null,
            _flight.Pictures.Underlay());
    }

    /// <summary>
    /// Show the tiles of the picture the screen shows as the camera moves, and draw those it is missing
    /// on <paramref name="ground"/>, from the one under the pointer outwards (see <see cref="ExploreTiles"/>):
    /// what comes into view is as sharp as the zoom it is seen at, rather than the drawing worked from
    /// magnified until the move stops.
    /// </summary>
    public void Sharpen(TileColour ground)
    {
        if (!IsShowing || !HasSize)
        {
            return;
        }

        var shown = _zoom.Shown;
        var (x, y) = _pointer() is { } pointer
            ? shown.PictureAt(pointer.X / _host.ActualWidth, pointer.Y / _host.ActualHeight)
            : shown.PictureAt(0.5, 0.5);

        _flight.Pictures.WantTiles(shown, _zoom.Target, x, y, ground);
    }

    /// <summary>
    /// Start a redraw of a drawing <paramref name="width"/> by <paramref name="height"/>, laid out by
    /// <paramref name="layout"/>: shaded on the GPU in one pass where the device can, and the drawing is of
    /// rectangles, and otherwise painted on the CPU a region at a time from <paramref name="focus"/> out.
    /// </summary>
    private CanvasRedraw Start(
        ExplorePainting painting, Func<ExploreSurface> layout, int width, int height, ExplorePoint? focus, ICanvasRedrawTarget target) =>
        _graphics.Shades && ExploreSurface.Drawn(painting.Tree, painting.Drawn.View) != ExploreView.Sunburst
            ? _redraws.Shade(layout, painting.Ground, target)
            : _redraws.Start(layout, _buffers.PixelsFor(width, height), painting.Ground, focus, target);

    /// <summary>
    /// How to lay out a drawing of <paramref name="painting"/> at <paramref name="viewport"/>, on a canvas
    /// of <paramref name="width"/> by <paramref name="height"/>.
    ///
    /// <para>Every value is taken now, on the UI thread, because the layout runs on a worker. Read
    /// there, the display scale and the text size would be read from the wrong thread, and could be
    /// read after the page had handed over another picture.</para>
    /// </summary>
    /// <param name="arrival">
    /// What the map will ask of the drawing as it arrives, worked out there too, or null for a drawing
    /// it is not handed (<see cref="DrawingArrival"/>).
    /// </param>
    private Func<ExploreSurface> Layout(ExplorePainting painting, MapViewport viewport, int width, int height, DrawingArrival? arrival)
    {
        var (tree, (_, node, view, volume), colours, look, _) = painting;
        var scale = Scale;
        var textScale = SystemLook.Current.State.TextScale;

        return () =>
        {
            var drawing = ExploreSurface.Create(
                tree, node, view, width, height, scale, textScale, colours, look.Spacing, volume, viewport, look.Style);

            arrival?.Prepare(drawing);

            return drawing;
        };
    }

    /// <summary>
    /// Where the pointer is on a canvas of <paramref name="width"/> by <paramref name="height"/>
    /// drawn at <paramref name="viewport"/>, so the region under it is painted first. Null where it is
    /// not over that canvas.
    /// </summary>
    private ExplorePoint? Focus(MapViewport viewport, int width, int height)
    {
        if (_pointer() is not { } pointer || !HasSize)
        {
            return null;
        }

        var (x, y) = _zoom.Shown.PlacementOf(viewport).InDrawing(pointer.X / _host.ActualWidth, pointer.Y / _host.ActualHeight);

        return x is >= 0 and < 1 && y is >= 0 and < 1
            ? new ExplorePoint((float)(x * width), (float)(y * height))
            : null;
    }

    /// <summary>
    /// How many canvas pixels a map extent of <paramref name="extent"/> device-independent pixels asks
    /// for. One expression, so that asking whether a drawing still fits the map gets the same answer a
    /// redraw would lay out to.
    /// </summary>
    private int DevicePixels(double extent) =>
        (int)Math.Round(extent * (_host.XamlRoot?.RasterizationScale ?? 1));
}
