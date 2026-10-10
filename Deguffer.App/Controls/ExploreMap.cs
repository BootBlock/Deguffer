using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Deguffer.App.Controls;

/// <summary>
/// Draws a scanned tree, and says what the pointer is over.
///
/// <para>The geometry is painted into a picture rather than kept as shapes. A full volume lays out to
/// tens of thousands of them, and the framework's own performance guidance is that a vector element
/// repeated enough times should become an image instead — which is also what every reference
/// implementation does, WinDirStat rendering into a cached surface and blitting it rather than
/// keeping a shape per file. The picture is in the composition layer: each drawing a surface written
/// a region at a time, placed in the whole picture once, and the whole picture placed on the screen
/// by one camera (<see cref="MapCamera"/>), so a move writes the camera and nothing else. A zoomed
/// picture keeps a few drawings, one per part of it drawn: see <see cref="ExploreLayers"/>.</para>
///
/// <para>Two things are not in the picture, and each has a part of its own that the camera moves
/// with it: <see cref="ExploreLabels"/> for the names, so they scale with the user's text size, and
/// <see cref="ExploreHighlight"/> for the lines round what is picked and what the pointer is over,
/// so a click moves a line rather than rasterising a volume again.</para>
///
/// <para>Nothing here knows what a drive is, how one is scanned, or how any of the views are laid
/// out. It is handed a tree, a node and a view, it asks Core for the matching
/// <see cref="ExploreSurface"/>, and it reports back what was pointed at (G1).</para>
///
/// <para>Over 500 lines because it is where the pointer, the drawings and the page meet, and each
/// input has to be resolved against whichever picture is on screen at that moment (§7.1). Everything
/// that can stand apart does: the drawings kept (<see cref="ExploreLayers"/>), the clocks of a zoom
/// and of a folder opening (<see cref="ExploreZoom"/>, <see cref="ExploreDescent"/>), the names and
/// the outlines, and in Core the arithmetic and the rule telling a click from a drag
/// (<see cref="MapDrag"/>). Most of the length is the reasoning behind each ordering.</para>
/// </summary>
public sealed class ExploreMap : UserControl
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

    private readonly PaintBuffers _buffers = new();

    /// <summary>
    /// Lays each drawing out and paints it off the UI thread, and puts it on screen a region at a
    /// time. See <see cref="CanvasRedraws"/>.
    /// </summary>
    private readonly CanvasRedraws _redraws;

    /// <summary>
    /// The redraw of the picture the map is to work from next, while it lands. Its regions answer for
    /// the points they cover until it arrives, and <see cref="_drawing"/> for every other point,
    /// because each is what the screen shows there (§7.1).
    /// </summary>
    private CanvasRedraw? _arriving;

    /// <summary>
    /// Whether a redraw was stopped by the map leaving the screen before it arrived, so the map owes
    /// one when it comes back.
    /// </summary>
    private bool _redrawOwed;

    /// <summary>
    /// Whether a folder has finished opening and the picture of it has not arrived yet. The old picture
    /// stays, stretched where the opening left it, until the new one has landed over it.
    /// </summary>
    private bool _openingOwed;

    /// <summary>
    /// Whether the last press was one a tap may act on: the left button, a touch or a pen. The
    /// framework makes a tap and a double-tap of the mouse's Back and Forward buttons too, which pick
    /// and open nothing anywhere else in Windows. Where the page goes back or forward with them,
    /// picking or opening the shape under the pointer as well would act on a picture the step is
    /// about to replace.
    /// </summary>
    private bool _tapPress = true;

    private readonly ExploreLabels _labels;

    private readonly ExploreHighlight _highlight;

    /// <summary>The device every drawing is written through. See <see cref="MapGraphics"/>.</summary>
    private readonly MapGraphics _graphics;

    /// <summary>Where the whole picture is on the screen: what each frame of a move writes for the picture to follow.</summary>
    private readonly MapCamera _camera;

    /// <summary>The map's composition tree: the two sets of drawings, then the outlines over them.</summary>
    private readonly ContainerVisual _root;

    /// <summary>
    /// How many times the device had been replaced when the drawings kept were written. See
    /// <see cref="MapGraphics.Generation"/>.
    /// </summary>
    private int _generation;

    private readonly DispatcherQueueTimer _settled;

    private readonly ExploreZoom _zoom = new(SystemMotion.Current, RenderingClock.Current);

    private readonly ExploreDescent _descent = new(SystemMotion.Current, RenderingClock.Current);

    /// <summary>The pointer's look while it drags the picture. One for the life of the map (G5).</summary>
    private readonly InputCursor _dragCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    /// <summary>
    /// The drawings of the picture on screen. See <see cref="ExploreLayers"/>. Swapped with
    /// <see cref="_departing"/> as a folder opens, so the old picture can go on showing while the new
    /// one grows over it.
    /// </summary>
    private ExploreLayers _pictures;

    /// <summary>The picture a folder is opening out of, while it does, and empty otherwise.</summary>
    private ExploreLayers _departing;

    /// <summary>
    /// The folder the page is being asked to open, and where its shape is on the screen, for the one
    /// moment the request is being made. See <see cref="OnDoubleTapped"/>.
    /// </summary>
    private (int Node, MapFrame Shape)? _opening;

    /// <summary>
    /// How many different pictures <see cref="Show(ISizedTree, int, ExploreView, Func{DateTime, ShapeColours}, Func{int, string}, ExploreSpacing, VolumeSpace)"/>
    /// has been handed, so a double-click can tell whether the page opened what it asked for.
    /// </summary>
    private int _picturesHanded;

    /// <summary>Whether the left button is dragging the picture. See <see cref="MapDrag"/>.</summary>
    private readonly MapDrag _drag = new();

    /// <summary>
    /// What everything drawn is cut to while the picture is zoomed. A zoomed picture's shapes, their
    /// outlines and the names in their bands run off the control's edges, and a transform draws them
    /// over whatever is beside it unless something stops it. Kept and resized rather than replaced (G5).
    /// </summary>
    private readonly RectangleGeometry _edges = new();

    private readonly Grid _layers;

    /// <summary>What the user picked, as a set because it is asked of every shape in the drawing.</summary>
    private readonly HashSet<int> _picked = [];

    /// <summary>
    /// The one node under the pointer, in the shape <see cref="ExploreSurface.Outlines"/> wants.
    /// Kept and rewritten rather than built per move: a pointer crosses a treemap's shapes many
    /// times a second, and each crossing would otherwise be an allocation (G5).
    /// </summary>
    private readonly HashSet<int> _under = [];

    private ISizedTree? _tree;
    private int _node;
    private ExploreView _view = ExploreView.Treemap;

    /// <summary>
    /// What the colours are to say, asked for at each repaint rather than held. See
    /// <see cref="ShapeColours.For"/>: an age band is relative to the moment it is drawn.
    /// </summary>
    private Func<DateTime, ShapeColours> _colours = _ => ShapeColours.ByBranch(ExploreScheme.Standard);

    /// <summary>
    /// What to write on a shape. The tree knows its own names, and what a name means differs between
    /// a drive and a picture of memory, so the page that owns the tree says it.
    /// </summary>
    private Func<int, string> _labelText = _ => string.Empty;

    /// <summary>How much room a treemap leaves round what each folder holds.</summary>
    private ExploreSpacing _spacing = ExploreSpacing.Comfortable;

    /// <summary>
    /// The volume to draw beside the node, or <see cref="VolumeSpace.None"/>; the page decides. See
    /// <see cref="ExploreSurface.Create(ISizedTree, int, ExploreView, int, int, double, double, ShapeColours, ExploreSpacing, VolumeSpace, MapViewport)"/>
    /// for where it is drawn.
    /// </summary>
    private VolumeSpace _volume = VolumeSpace.None;

    /// <summary>
    /// The reader's Windows text size, which the labels grow with and the layout has to leave room
    /// for, and the accent colour the hover outline is drawn in. One instance for every map, because
    /// it is a window onto the system's settings (G5).
    /// </summary>
    private static readonly UISettings SystemSettings = new();

    /// <summary>
    /// Where the pointer was last seen, in this control's coordinates, or null while it is elsewhere.
    /// Kept so a redraw can say what is under it again: a page that refreshes on its own would
    /// otherwise drop the outline and the readout under a pointer that never moved.
    /// </summary>
    private Point? _pointer;

    private ExploreSurface? _drawing;

    /// <summary>
    /// The part of the picture <see cref="_drawing"/> shows, which is the whole of it unless the
    /// drawing said otherwise. See <see cref="ExploreSurface.Viewport"/>.
    /// </summary>
    private MapViewport _drawn;

    /// <summary>
    /// What the colours say in every drawing of the picture on screen, taken once per picture from
    /// <see cref="_colours"/>. A zoom that stops later draws in the same colours as the drawings kept
    /// under it, or an age band could change at the edge of one.
    /// </summary>
    private ShapeColours? _shapeColours;

    private double _scale = 1;
    private ExploreHit? _hovered;

    /// <summary>Whether a node has gone since the scan. See <see cref="Excluding"/>.</summary>
    private Func<int, bool> _gone = _ => false;

    public ExploreMap()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

        _graphics = MapGraphics.For(compositor);
        _generation = _graphics.Generation;
        _camera = new MapCamera(compositor);
        _pictures = new ExploreLayers(compositor, _graphics, _camera);
        _departing = new ExploreLayers(compositor, _graphics, _camera);
        _highlight = new ExploreHighlight(compositor, _graphics, _camera);
        _labels = new ExploreLabels(_camera, _pictures.Root, SystemMotion.Current);
        _redraws = new CanvasRedraws(new DispatcherQueueSynchronizationContext(DispatcherQueue));

        _pictures.Follow();

        // The outlines go over the picture, and the labels over both. A label is inset from its
        // shape's edge and an outline runs along it, so the two rarely meet — and where they do, the
        // name of the thing is worth more than the last pixel of the line round it. The picture a
        // folder is opening out of goes under the one opening, which grows over it.
        _root = compositor.CreateContainerVisual();
        _root.Children.InsertAtTop(_departing.Root);
        _root.Children.InsertAtTop(_pictures.Root);
        _root.Children.InsertAtTop(_highlight.Root);

        // The composition tree is hosted under an element of its own, so the labels, which are XAML,
        // can go over it: a hosted visual is drawn over its host's children.
        var picture = new Border();
        ElementCompositionPreview.SetElementChildVisual(picture, _root);

        // The ground is transparent rather than absent, so the whole control takes the pointer. The
        // picture itself takes none of it, so what is under the pointer is always this control's own
        // answer rather than whichever drawing happens to be topmost there.
        _layers = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children = { picture, _labels },
        };
        Content = _layers;

        _zoom.Moved += OnZoomMoved;
        _zoom.Arrived += (_, _) =>
        {
            OnZoomArrived();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        };
        _zoom.Retargeted += (_, _) => ViewportChanged?.Invoke(this, EventArgs.Empty);

        _descent.Moved += OnDescentMoved;
        _descent.Arrived += OnDescentArrived;

        _settled = DispatcherQueue.CreateTimer();
        _settled.Interval = ResizeSettleTime;
        _settled.IsRepeating = false;
        _settled.Tick += (_, _) => Redraw();

        SizeChanged += OnSizeChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnDragEnded;
        PointerCanceled += OnDragEnded;
        PointerCaptureLost += OnDragEnded;
        PointerExited += OnPointerExited;
        PointerWheelChanged += OnPointerWheelChanged;
        DoubleTapped += OnDoubleTapped;

        // Picking is separate from opening, on the file-manager idiom: one click says which one, two
        // says go in. A right-click picks as well, so the menu that follows is about the shape under
        // the pointer rather than about whatever was picked last.
        Tapped += OnTapped;
        RightTapped += OnRightTapped;

        // Dragged to a differently scaled display, the control's size in device-independent units
        // does not change, so nothing above fires and the canvas stays at the old resolution.
        // The scale is read from the XamlRoot rather than from this element: the identically
        // documented property on UIElement does not update on a scale change
        // (microsoft-ui-xaml #9610), and the root is not attached until this is loaded.
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root)
            {
                root.Changed += OnRootChanged;
            }

            SystemSettings.TextScaleFactorChanged += OnTextScaleChanged;
            SystemSettings.ColorValuesChanged += OnSystemColoursChanged;
            TintHighlight();

            _graphics.Replaced += OnDeviceReplaced;

            // The device was replaced while the map was away, so nothing it kept is on its surfaces
            // any more, and every redraw the rest of this would ask for is owed in full.
            if (_generation != _graphics.Generation)
            {
                OnDeviceReplaced(_graphics, EventArgs.Empty);
                return;
            }

            // A resize that arrived while this was on screen, and was still waiting to be drawn
            // when the page was navigated away from, is dropped below rather than rasterised for a
            // page nobody is looking at. Coming back at that same size raises no SizeChanged, so
            // this is the only place that owes the redraw — and without it the map stays stretched
            // over the old canvas for as long as the page is open.
            //
            // The same goes for a zoom that was still moving when the page was left. It was finished
            // where it was going rather than eased for nobody, so the drawing is of where it was, and
            // the drawings kept of the rest of the picture are still good for the one it went to.
            //
            // A redraw stopped as the page was left is owed in full, because the picture it was
            // drawing has not been on screen.
            if (_redrawOwed)
            {
                Redraw();
            }
            else if (_drawing is { } drawing)
            {
                if (drawing.Width != DevicePixels(ActualWidth) || drawing.Height != DevicePixels(ActualHeight))
                {
                    Redraw();
                }
                else if (_drawn != _zoom.Shown)
                {
                    OnZoomArrived();
                }
            }
        };

        Unloaded += (_, _) =>
        {
            // A pending redraw for a size this control is no longer showing at. Left running it
            // would rasterise a whole volume for a page that has been navigated away from. A folder
            // still opening, or a drag still held, is finished where it was going for the same reason.
            _settled.Stop();
            _zoom.Stop();
            _descent.Finish();
            EndDrag();

            // A redraw still landing is stopped for the same reason, and owed when the page is back.
            // One painting the whole picture under a zoomed drawing is not owed: it is asked for
            // again when the picture next moves.
            _redrawOwed |= _arriving is { IsSettled: false };
            _redraws.Cancel();

            // The page is kept alive while it is away (NavigationCacheMode), so what it holds stays
            // held: at 4K, 33 MB for each drawing kept and for the paint buffer. Only the drawing on
            // screen is needed to come back to, and the rest is asked for again when the picture next
            // moves.
            _pictures.Trim();
            _buffers.Release();

            // The labels go back with it. Dropping the redraw drops the thing that would have put
            // them back, and Loaded only redraws where the size has actually moved — so without
            // this a map returned to at the size it was last drawn at comes back with no names on
            // it at all, and nothing on the page would put them there.
            _labels.Reveal();

            if (XamlRoot is { } root)
            {
                root.Changed -= OnRootChanged;
            }

            SystemSettings.TextScaleFactorChanged -= OnTextScaleChanged;
            SystemSettings.ColorValuesChanged -= OnSystemColoursChanged;
            _graphics.Replaced -= OnDeviceReplaced;
        };

        // The ground is baked into the canvas, so unlike every themed control around it the map
        // cannot restyle itself. Nothing else here fires on a theme switch — the page is kept alive
        // by NavigationCacheMode, so a trip to Settings and back does not rebuild it either — and
        // the map would keep the old ground until the window was resized. WindowBackdrop subscribes
        // this same event for the same reason.
        ActualThemeChanged += (_, _) => Redraw();

        // The map is one focusable thing rather than one per shape, and the status line beside it
        // carries what is under the pointer. A screen reader needs the same information without a
        // pointer, which the list view provides in full — so this announces its role and defers.
        IsTabStop = true;
        AutomationProperties.SetName(this, "Map of what was scanned");

        // Naming where the same content is readable, because deferring to the list view only helps
        // somebody who knows it is there. It is one of four options in the View picker and it is
        // not the default.
        DescribeControls();
    }

    /// <summary>
    /// The node the user asked to open, by double-clicking a shape. A page that opens it by showing it
    /// here, before this returns, has it open out of its shape on a treemap. See
    /// <see cref="OnDoubleTapped"/>.
    /// </summary>
    public event EventHandler<int>? Activated;

    /// <summary>
    /// A double-click is zooming to a shape it could not open, away from this part of the picture.
    /// A step the page can go back from, as it can from a folder opened; a turn of the wheel or a drag
    /// is not one.
    /// </summary>
    public event EventHandler<MapViewport>? ZoomingIn;

    /// <summary>
    /// The part of the picture the reader asked for: where the zoom is going, or where it is when
    /// nothing is moving it. The whole picture on a drawing that does not zoom.
    /// </summary>
    public MapViewport Viewport => _zoom.Target;

    /// <summary>
    /// <see cref="Viewport"/> has a new answer: a glide set off, a drag let go, or the zoom was put
    /// back for another picture or for a drawing that cannot be zoomed.
    /// </summary>
    public event EventHandler? ViewportChanged;

    /// <summary>
    /// Whether the mouse wheel zooms the picture and the left button drags it, where the drawing can
    /// be zoomed at all. Off unless the page asks, because every new tree starts from the whole
    /// picture, and a page that draws a new tree every few seconds would take the reader's zoom away
    /// every few seconds.
    /// </summary>
    public bool Zoomable
    {
        get;
        set
        {
            field = value;
            DescribeControls();
        }
    }

    /// <summary>
    /// Say what the map is and how the pointer moves it, for a screen reader on the focused map:
    /// the caption naming the same on the page is only reached by browsing to it.
    /// </summary>
    private void DescribeControls() =>
        AutomationProperties.SetHelpText(
            this,
            "A picture of what is using the space. Choose List in the View box for the same "
            + "contents as a readable list. Double-click a shape to open what is inside it."
            + (Zoomable
                ? " On the treemap, turn the mouse wheel to zoom, drag with the left button to move a "
                    + "zoomed picture, and double-click anything else to zoom to it. The mouse's Back and "
                    + "Forward buttons, Backspace, Alt+Left and Alt+Right step back and forward through "
                    + "the folders opened and the zooms to a shape."
                : string.Empty));

    /// <summary>
    /// The node the user picked out by hand, or null where they clicked nothing.
    ///
    /// <para>Null matters: clicking empty space clears the selection, and §7.1 requires Explore to
    /// act only on what was picked. A pick that could not be cleared would leave a stale one behind
    /// for the menu to act on.</para>
    /// </summary>
    public event EventHandler<int?>? Picked;

    /// <summary>
    /// The user asked for the menu, at this point in the control's own coordinates. Raised after
    /// <see cref="Picked"/>, so the menu opens on a selection that already matches the pointer.
    /// </summary>
    public event EventHandler<Point>? MenuRequested;

    /// <summary>
    /// What the pointer moved over: a node, or one of the two blocks that stand for something other
    /// than a node. Null when it is over nothing.
    /// </summary>
    public event EventHandler<ExploreHit?>? Hovered;

    /// <summary>
    /// Draw <paramref name="node"/> of a scanned <paramref name="tree"/> in <paramref name="view"/>,
    /// with the shapes coloured to say <paramref name="colouring"/> in <paramref name="scheme"/> and
    /// labelled with a name and a size.
    /// </summary>
    /// <param name="volume">
    /// The volume to draw beside <paramref name="node"/>, where the reader is on a volume the scan
    /// covered the whole of, and <see cref="VolumeSpace.None"/> otherwise.
    /// </param>
    /// <param name="growth">What grew since the last scan, for a map coloured by growth, or null.</param>
    /// <param name="types">The kind of file each node holds most of, for a map coloured by type, or null.</param>
    /// <param name="viewport">See the other overload.</param>
    public void Show(
        ExploreTree? tree,
        int node,
        ExploreView view,
        ExploreColouring colouring,
        ExploreScheme scheme,
        ExploreSpacing spacing,
        VolumeSpace volume,
        Core.Exploring.History.ScanGrowth? growth,
        Core.Exploring.Files.DominantTypes? types,
        MapViewport? viewport = null) =>
        Show(
            tree,
            node,
            view,
            tree is null
                ? _ => ShapeColours.ByBranch(scheme)
                : now => ShapeColours.For(tree, colouring, scheme, now, growth, types),
            tree is null
                ? _ => string.Empty
                : drawn => $"{tree.NameOf(drawn)}  {FreeSpace.Format(tree.SizeOf(drawn))}",
            spacing,
            volume,
            viewport);

    /// <summary>
    /// Draw <paramref name="node"/> of any tree a layout can lay out.
    /// </summary>
    /// <param name="colours">What the colours are to say, asked at each repaint.</param>
    /// <param name="labelText">What to write on a shape of the tree this drawing chose to label.</param>
    /// <param name="spacing">How much room a treemap leaves round what each folder holds.</param>
    /// <param name="volume">The volume to draw beside <paramref name="node"/>, or <see cref="VolumeSpace.None"/>.</param>
    /// <param name="viewport">
    /// The zoom to show the picture at, for a page going back or forward to where the reader had
    /// zoomed, and null to keep the zoom on the same picture and start another at the whole of it.
    /// </param>
    public void Show(
        ISizedTree? tree,
        int node,
        ExploreView view,
        Func<DateTime, ShapeColours> colours,
        Func<int, string> labelText,
        ExploreSpacing spacing,
        VolumeSpace volume,
        MapViewport? viewport = null)
    {
        ArgumentNullException.ThrowIfNull(colours);
        ArgumentNullException.ThrowIfNull(labelText);

        // A zoom is into one picture. Another tree, another folder or another view is another
        // picture, and the part of this one the reader had magnified means nothing in it. So is the
        // same root with the volume beside it or without it, which is the root opened or closed. A
        // new colouring, scheme or spacing is the same picture, so the zoom stays.
        var was = _zoom.Shown;
        var another = !ReferenceEquals(tree, _tree) || node != _node || view != _view || volume != _volume;

        // A shape opened by a double-click, in the same tree and the same view, opens out of that
        // shape: a folder, or the root opened out of the volume beside it. Anything else arriving
        // here is not what the double-click asked for, and nor is the same picture again, which
        // has nothing to open into.
        var opening = another && _opening is { } asked && asked.Node == node && ReferenceEquals(tree, _tree) && view == _view
            ? asked.Shape
            : (MapFrame?)null;

        if (another)
        {
            _picturesHanded++;

            _descent.Finish();
            EndDrag();

            // Drawn at the zoom asked for from the start, rather than drawn whole and then glided in.
            _zoom.Reset(Zoomable && viewport is { } revisited && tree is not null && ExploreSurface.Zooms(tree, view)
                ? revisited
                : MapViewport.Whole);

            // The picture on screen stays, to open out of, and the new one is drawn into the other
            // set of layers over it.
            if (opening is not null && _drawing is not null)
            {
                (_pictures, _departing) = (_departing, _pictures);

                // The old picture stays where the camera had it as the folder was double-clicked,
                // and the camera goes on to the new one, which grows over it.
                _departing.Freeze(was.Camera(ActualWidth, ActualHeight));
                _pictures.Follow();
                _pictures.Fit(ActualWidth, ActualHeight);
                _labels.Ride(_pictures.Root);

                _root.Children.Remove(_departing.Root);
                _root.Children.InsertAtBottom(_departing.Root);

                // The drawing on screen is the one being opened out of now, carried away with the
                // old picture, so nothing is resolved against it again. The pointer is over nothing
                // until the folder's own drawing arrives.
                _drawing = null;
            }
            else
            {
                opening = null;
            }
        }

        _tree = tree;
        _node = node;
        _view = view;
        _colours = colours;
        _labelText = labelText;
        _spacing = spacing;
        _volume = volume;

        // Started before the new picture is drawn, so the drawing arrives into a folder already
        // opening: its outlines wait for it the way they wait for a zoom, its names ride it in, and
        // the pointer is over nothing until the picture it is over is the one on screen. The names
        // of the picture opened out of go with it.
        if (opening is { } shape)
        {
            _labels.Hide();
            _highlight.Hide();
            _descent.Start(shape);
        }

        Redraw();

        // Nothing to open into after all: a folder with nothing in it to draw.
        if (opening is not null && _drawing is null && _redraws.Pending is null)
        {
            _descent.Finish();
        }

        // The same picture, zoomed to where the reader was, moves there as a zoom to a shape does.
        // After the redraw, which draws where the zoom is now and is the drawing the move starts from.
        if (!another && Zoomable && viewport is { } returning && tree is not null && ExploreSurface.Zooms(tree, view))
        {
            _zoom.GlideTo(returning);
        }
    }

    /// <summary>
    /// Mark <paramref name="nodes"/> out on the picture, as what the user picked.
    ///
    /// <para>Told rather than remembered, because the selection is not the map's to hold. The list
    /// view selects the same things, a scan snapshot carries some of them and drops the rest, and a
    /// removal empties it or drops what it took — so the one copy that decides what Delete acts on lives in the view
    /// model, and this draws whatever that copy currently says (§7.1).</para>
    /// </summary>
    public void Select(IReadOnlyList<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        if (_picked.SetEquals(nodes))
        {
            return;
        }

        _picked.Clear();
        _picked.UnionWith(nodes);

        ShowPicked();

        // What the pointer is over may have just become what is picked, in which case it stops
        // being outlined separately. See ShowHovered.
        ShowHovered();
    }

    /// <summary>
    /// How to tell whether a node has gone since the scan, so the map stops offering it.
    ///
    /// <para>The rule rather than a list of nodes, and told rather than worked out. A removal takes
    /// everything inside it, so the list would be every file under a deleted directory; and §7.1
    /// allows one answer to "may this be acted on", which is <see cref="ExploreSelection"/>'s to
    /// give. A second copy here would be a second answer.</para>
    ///
    /// <para>Given once, because the rule does not change — only what it answers, as things are
    /// removed.</para>
    /// </summary>
    public void Excluding(Func<int, bool> gone)
    {
        ArgumentNullException.ThrowIfNull(gone);

        _gone = gone;

        ShowHovered();
    }

    /// <summary>
    /// Wait for the drag to stop before redrawing, on the terms
    /// <see cref="ResizeSettleTime"/> gives.
    ///
    /// <para>The first size the control is ever given is drawn at once. There is no drawing to
    /// stretch in the meantime, so deferring that one would leave the panel empty for the length of
    /// the wait every time the page is opened.</para>
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Every drawing kept stretches over the new size, and so does one landing.
        Fit();

        if (_drawing is null)
        {
            Redraw();
            return;
        }

        // The picture stretches on each axis apart, and a name the layout gave room to at the old size
        // can run over its neighbours at the new one. They come back with the layout for it.
        _labels.Hide();

        // The outlines stretch with it too, and a polygon scales exactly where a line of text does
        // not, so they go on marking out the same shapes throughout the drag.
        Place();

        // Stopped and started rather than started, so each size change puts the whole wait back and
        // a drag that is still moving never reaches the end of one.
        _settled.Stop();
        _settled.Start();
    }

    /// <summary>
    /// Put the picture where the zoom on screen says it is: the camera, which every drawing, the
    /// outlines and the names follow, and the clip at the map's edges while anything runs past them.
    /// What each frame of a move writes.
    /// </summary>
    private void Place()
    {
        // Cut only while something can run past the edges. Unzoomed, nothing does except the halo of
        // an outline round a shape at the edge, which has always been drawn whole.
        var cut = !(_zoom.Shown.IsWhole && _drawn.IsWhole && !_descent.IsMoving);

        if (cut)
        {
            _edges.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        }

        _layers.Clip = cut ? _edges : null;

        _camera.Show(_zoom.Shown.Camera(ActualWidth, ActualHeight));
    }

    /// <summary>
    /// Put each drawing, and the outlines and the names of the one worked from, where they lie in the
    /// picture at the control's size now. For a new size or a new drawing: a move changes only the
    /// camera.
    /// </summary>
    private void Fit()
    {
        _pictures.Fit(ActualWidth, ActualHeight);

        if (_drawing is not { } drawing)
        {
            return;
        }

        _highlight.PlaceOver(
            _drawn.Canvas(drawing.Width, drawing.Height, ActualWidth, ActualHeight),
            ActualWidth,
            ActualHeight);

        _labels.Place(_drawn.Labels(drawing.Width, drawing.Height, ActualWidth, ActualHeight, _scale));
    }

    private void Redraw()
    {
        // Whatever brought us here is more current than a size change still waiting to be drawn, or a
        // redraw stopped when the page was left.
        _settled.Stop();
        _redrawOwed = false;

        // Nothing can see it, and the page asks again as it brings the map back. ExplorePage
        // collapses this for the List view and calls Show() in the same breath, so without this a
        // switch to List rasterises a whole volume into a control nobody is looking at.
        //
        // What was drawn is dropped rather than kept, because the page brings the map back with a
        // Show() that draws it again anyway, and the zoomed drawings are 33 MB each at 4K. The
        // paint buffer stays, and so do the layers with their surfaces emptied, so switching back
        // allocates neither (G5).
        if (Visibility != Visibility.Visible)
        {
            _redraws.Cancel();
            _pictures.Clear();
            _drawing = null;
            _hovered = null;
            return;
        }

        _scale = XamlRoot?.RasterizationScale ?? 1;

        var width = DevicePixels(ActualWidth);
        var height = DevicePixels(ActualHeight);

        if (_tree is null || width <= 0 || height <= 0 || _tree.SizeOf(_node) <= 0)
        {
            // Every drawing goes, and the buffer they were painted through: a map with nothing to
            // show holds no memory for it.
            _redraws.Cancel();
            _pictures.Clear();
            _buffers.Release();
            _drawing = null;
            _shapeColours = null;
            _hovered = null;
            _labels.Clear();
            _highlight.Clear();

            // There is no picture now, so nothing is under the pointer. Said rather than left: a
            // cancelled scan takes the tree away, and without this the line under the map goes on
            // naming whatever was last hovered over a blank canvas.
            Report(null);

            // A folder opened into nothing: the picture it opened out of goes too.
            if (_openingOwed)
            {
                FinishOpening();
            }

            return;
        }

        // The clock is read here rather than held, because the age bands are relative to now and
        // a map left on screen overnight would otherwise keep yesterday's answer. A repaint costs
        // one read of it against a full rasterisation.
        _shapeColours = _colours(DateTime.UtcNow);

        // Every drawing kept is of the picture as it was, so none of them is shown again, though
        // they stay on screen until the new one has landed over them. Drawn at the zoom on screen
        // now, which is partway through a move if one is on its way: a repaint mid-move is placed
        // for the rest of the move like any other drawing.
        _pictures.Forget();

        Request(_zoom.Shown);
    }

    /// <summary>
    /// Show the picture where a zoom or a drag stopped: the drawing kept of that part of it, where
    /// there is one, and a new one otherwise. The picture has not changed, so every drawing already
    /// made of it still stands.
    /// </summary>
    private void OnZoomArrived()
    {
        // Left for Loaded, which shows the arrival when the map is back, as it does a redraw.
        if (!IsLoaded || Visibility != Visibility.Visible || _drawing is not { } drawing)
        {
            return;
        }

        // A resize still settling has made every drawing kept the wrong size, and its names would
        // go back over a stretched picture at the old size's positions. The whole picture is drawn
        // again at the new one instead, which is what the settling was waiting to do.
        if (_settled.IsRunning
            || drawing.Width != DevicePixels(ActualWidth)
            || drawing.Height != DevicePixels(ActualHeight))
        {
            Redraw();
            return;
        }

        // Nothing new to draw, and the names are still where this drawing put them. Unless a redraw
        // is landing, which was asked for somewhere the zoom has since left, or of a picture that
        // has changed since this drawing was made.
        if (_zoom.Shown == _drawn && _arriving is not { IsSettled: false })
        {
            _labels.Reveal();
            Place();
            return;
        }

        Request(_zoom.Shown);
    }

    /// <summary>
    /// Show <paramref name="viewport"/> of the picture: the drawing kept of it, at once, where there
    /// is one, and otherwise a redraw of it, which lands over the picture on screen.
    /// </summary>
    private void Request(MapViewport viewport)
    {
        if (_pictures.Show(viewport) is { } kept)
        {
            _redraws.Cancel();
            Present(kept);
            return;
        }

        // The screen is put where the zoom now says before anything lands on it. A zoom can change
        // with nothing moving the drawings, as a new picture resets it and a page left mid-glide
        // finishes it, and every point is resolved through the zoom (§7.1): until the new drawing
        // arrives, the old one answers, and it has to be shown where it is resolved.
        Place();

        var width = DevicePixels(ActualWidth);
        var height = DevicePixels(ActualHeight);

        _arriving = _redraws.Start(
            Layout(viewport, width, height),
            _buffers.PixelsFor(width, height),
            Ground(),
            Focus(viewport, width, height),
            _pictures.Arrival(onTop: true, Present));
    }

    /// <summary>
    /// How to lay out a drawing of the picture on screen at <paramref name="viewport"/>, on a canvas
    /// of <paramref name="width"/> by <paramref name="height"/>.
    ///
    /// <para>Every value is taken now, on the UI thread, because the layout runs on a worker. Read
    /// there, the control's size, the text size and the tree would be read from the wrong thread, and
    /// could be read after the page had handed over another picture.</para>
    /// </summary>
    private Func<ExploreSurface> Layout(MapViewport viewport, int width, int height)
    {
        var tree = _tree!;
        var node = _node;
        var view = _view;
        var scale = _scale;
        var textScale = SystemSettings.TextScaleFactor;
        var colours = _shapeColours!;
        var spacing = _spacing;
        var volume = _volume;

        return () => ExploreSurface.Create(
            tree, node, view, width, height, scale, textScale, colours, spacing, volume, viewport);
    }

    /// <summary>
    /// Where the pointer is on a canvas of <paramref name="width"/> by <paramref name="height"/>
    /// drawn at <paramref name="viewport"/>, so the region under it is painted first. Null where it is
    /// not over that canvas.
    /// </summary>
    private ExplorePoint? Focus(MapViewport viewport, int width, int height)
    {
        if (_pointer is not { } pointer || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return null;
        }

        var (x, y) = _zoom.Shown.PlacementOf(viewport).InDrawing(pointer.X / ActualWidth, pointer.Y / ActualHeight);

        return x is >= 0 and < 1 && y is >= 0 and < 1
            ? new ExplorePoint((float)(x * width), (float)(y * height))
            : null;
    }

    /// <summary>Work from <paramref name="drawing"/>, which is on top of the rest now.</summary>
    private void Present(ExploreSurface drawing)
    {
        _drawing = drawing;

        // A drawing that cannot be zoomed shows the whole picture whatever was asked, and the zoom
        // has to agree with it: the screen is placed, and every click resolved, by what was drawn
        // rather than by what was asked for (§7.1).
        if (drawing.Viewport is { } viewport)
        {
            _drawn = viewport;
        }
        else
        {
            _zoom.Reset(MapViewport.Whole);
            _drawn = MapViewport.Whole;
        }

        // Over a folder still opening, too: the names ride the picture they name as it grows.
        _labels.Show(drawing, _scale, LabelText);

        // A new drawing is new geometry, so whatever was marked out is marked out somewhere else
        // now, and so is whatever the pointer is over.
        Fit();
        Place();
        ShowPicked();
        ReportWhatThePointerIsOver();

        // A folder that finished opening before its picture arrived finishes now, over it.
        if (_openingOwed && !_descent.IsMoving)
        {
            FinishOpening();
        }

        // The whole picture under a zoomed drawing only shows once the picture moves, so it waits
        // until the drawing the reader asked for is on screen. Asked again as a move starts, for a
        // move that comes first.
        if (drawing.Viewport is { IsWhole: false })
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Underlay);
        }
    }

    /// <summary>
    /// Paint the whole picture under a zoomed drawing, where it is not there already, so a move has
    /// something to show wherever it goes. See <see cref="ExploreLayers"/>.
    ///
    /// <para>Never in place of a redraw still landing, which is the picture the reader asked for: the
    /// two would share the one redraw at a time, and this one would stop that one. It is asked for
    /// again once that one has arrived.</para>
    /// </summary>
    private void Underlay()
    {
        // Not while a resize is settling: painted at the new size, it would be thrown away with every
        // other drawing when the settling redraws the picture.
        if (!IsLoaded
            || Visibility != Visibility.Visible
            || _drawing is not { } drawing
            || _settled.IsRunning
            || _redraws.Pending is not null
            || !_pictures.LacksWhole)
        {
            return;
        }

        _redraws.Start(
            Layout(MapViewport.Whole, drawing.Width, drawing.Height),
            _buffers.PixelsFor(drawing.Width, drawing.Height),
            Ground(),
            focus: null,
            _pictures.Arrival(onTop: false, static _ => { }));
    }

    /// <summary>
    /// Say what is under the pointer in the drawing that has just replaced the last one, and mark it.
    ///
    /// <para>Asked again rather than dropped, because a page that redraws on its own leaves the pointer
    /// where it was: a scan publishing a snapshot, or a memory view refreshing every couple of seconds.
    /// Dropping it took the outline and the readout away from a reader who had not moved, until they
    /// moved. Nothing is raised while the answer has not changed, so a redraw that leaves the same shape
    /// under the pointer costs nothing.</para>
    ///
    /// <para>That last part holds only because this control is the only thing that writes the readout.
    /// A page that also cleared it would blank its own line and never hear otherwise, and the outline
    /// drawn below would then mark a shape nothing named.</para>
    /// </summary>
    private void ReportWhatThePointerIsOver()
    {
        var hit = _pointer is { } pointer ? At(pointer) : null;

        if (hit == _hovered)
        {
            ShowHovered();
            return;
        }

        _hovered = hit;

        ShowHovered();
        Report(hit);
    }

    /// <summary>
    /// How many canvas pixels a control extent of <paramref name="extent"/> device-independent
    /// pixels asks for. One expression, so that a caller asking whether the drawing still matches
    /// the control gets the same answer <see cref="Redraw"/> would lay out to.
    /// </summary>
    private int DevicePixels(double extent) =>
        (int)Math.Round(extent * (XamlRoot?.RasterizationScale ?? 1));

    /// <summary>
    /// The canvas ground, taken from the theme rather than fixed.
    ///
    /// <para>§6.5 requires the UI to read correctly on a flat background in either theme, and this
    /// is where the reference implementation took the shortcut this cannot: WinDirStat's newer views
    /// hard-code a near-black ground and are dark whatever the system is set to.</para>
    /// </summary>
    /// <para>Read from <see cref="FrameworkElement.ActualTheme"/> rather than by pulling the brush
    /// out of the application's resource dictionary. This app applies the user's choice at element
    /// level — <c>MainWindow</c> sets <c>RequestedTheme</c> on the content root and nothing ever
    /// sets it on the application — so the application dictionary answers for the *system* theme.
    /// Asking it for a colour gives a light ground behind a dark page whenever the two disagree,
    /// which is the §6.5 failure this method exists to avoid.</para>
    private TileColour Ground() => ActualTheme == ElementTheme.Dark
        ? new TileColour(32, 32, 32)
        : new TileColour(243, 243, 243);

    /// <summary>
    /// What to write on a labelled shape. The blocks standing for the rest of the volume are the
    /// map's own, because they are not nodes of the page's tree.
    /// </summary>
    private string LabelText(ExploreLabel label) => label.Node switch
    {
        ExploreTile.FreeSpace => $"Free space  {FreeSpace.Format(label.Bytes)}",
        ExploreTile.Unaccounted => $"Not accounted for  {FreeSpace.Format(label.Bytes)}",
        ExploreTile.ShadowCopies => $"Restore points and shadow copies  {FreeSpace.Format(label.Bytes)}",
        ExploreTile.ReservedStorage => $"Reserved storage  {FreeSpace.Format(label.Bytes)}",
        _ => _labelText(label.Node),
    };

    /// <summary>Draw the outline round whatever is picked and this drawing actually drew.</summary>
    private void ShowPicked() =>
        _highlight.ShowPicked(Outlining is { } drawing ? drawing.Outlines(_picked) : []);

    /// <summary>
    /// The drawing the outlines are drawn in, which is the one the map works from, while it is of the
    /// tree the page handed over last. What is picked is named in that tree, and the same numbers in
    /// an older one are other shapes, so a drawing of an older tree, still on screen while the new one
    /// lands, marks nothing out until the new one arrives.
    /// </summary>
    private ExploreSurface? Outlining =>
        _drawing is { } drawing && ReferenceEquals(drawing.Tree, _tree) ? drawing : null;

    /// <summary>
    /// Draw the accent outline round whatever the pointer is over.
    ///
    /// <para>Four shapes get nothing. One already picked would carry two outlines, leaving the
    /// weaker claim on top of the stronger one. The other three cannot be picked at all (§7.1), so
    /// marking any of them out would invite a click that selects nothing: the block standing in for
    /// items too small to draw, the block standing for free space, and anything removed since the
    /// scan, which the picture goes on showing because the tree behind it is not rebuilt for a
    /// deletion.</para>
    /// </summary>
    private void ShowHovered()
    {
        _under.Clear();

        if (_hovered is { IsNode: true } hit
            && !_picked.Contains(hit.Node)
            && !_gone(hit.Node))
        {
            _under.Add(hit.Node);
        }

        _highlight.ShowHovered(
            _under.Count > 0 && Outlining is { } drawing ? drawing.Outlines(_under) : []);
    }

    /// <summary>
    /// What is under <paramref name="point"/>, in the control's own coordinates.
    ///
    /// <para>Mapped through the drawing's own dimensions rather than through the display scale,
    /// because the two part company for as long as a resize is still settling. The canvas is
    /// stretched over the control's new bounds in the meantime, so what is on screen is the old
    /// canvas scaled to fit, and a pointer mapped by the display scale would answer from geometry
    /// that is no longer where it is drawn.</para>
    ///
    /// <para>§7.1 makes that a safety question rather than a cosmetic one. A right-click picks
    /// what the menu then acts on, so a pick that disagrees with the picture is a Delete aimed at
    /// something the user never pointed at — the same mistake <c>ExplorePage</c> avoids by picking
    /// from the row under the pointer rather than from the last selection.</para>
    ///
    /// <para>A zoom or a drag on its way is the same case again. The screen shows the drawing moved
    /// and magnified, so the point is taken back through that placement first. A point the moved
    /// drawing does not reach is over nothing, even where a shape running off the drawing's edge would
    /// contain it, and even though a kept drawing shows there: that one is at another zoom, it names
    /// fewer shapes than the drawing that replaces it a moment later, and the outline that would
    /// confirm the pick is drawn in this drawing's geometry, not in its. A folder opening is over
    /// nothing throughout, for the same reason: the picture is on its way to being another one.</para>
    ///
    /// <para>A redraw landing is the case once more. Its regions show where they have landed and the
    /// last drawing shows everywhere else, so each point is answered by whichever shows there: see
    /// <see cref="MapHitTest"/>, which also refuses an answer from a drawing of an older tree.</para>
    /// </summary>
    private ExploreHit? At(Point point) => Locate(point) is { } spot ? spot.Drawing.At(spot.X, spot.Y) : null;

    /// <summary>
    /// Where <paramref name="point"/>, in the control's own coordinates, falls in the drawing that
    /// shows there, or null where none does. See <see cref="At"/>.
    /// </summary>
    private MapLocation? Locate(Point point) =>
        ActualWidth <= 0 || ActualHeight <= 0 || _descent.IsMoving
            ? null
            : MapHitTest.Locate(
                _zoom.Shown, point.X / ActualWidth, point.Y / ActualHeight, _drawing, _arriving, _tree);

    /// <summary>
    /// Where the shape at <paramref name="spot"/> is on the screen, in fractions of it, or null where
    /// there is no shape there or it is not a rectangle.
    /// </summary>
    private MapFrame? ShapeAt(MapLocation spot)
    {
        var drawing = spot.Drawing;

        if (drawing.TileAt(spot.X, spot.Y) is not { } tile)
        {
            return null;
        }

        return _zoom.Shown.PlacementOf(spot.Viewport).OnScreen(new MapFrame(
            tile.X / (double)drawing.Width,
            tile.Y / (double)drawing.Height,
            tile.Width / (double)drawing.Width,
            tile.Height / (double)drawing.Height));
    }

    /// <summary>
    /// Zoom at the pointer, where the page allows it and the picture can be zoomed.
    ///
    /// <para>Left unhandled otherwise, so a wheel over a map that does not zoom goes on to whatever
    /// would have had it. A horizontal wheel is not a zoom either.</para>
    /// </summary>
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!Zoomable || _drawing is not { Viewport: not null } || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsHorizontalMouseWheel)
        {
            return;
        }

        _descent.Finish();
        Underlay();

        _pointer = point.Position;
        _zoom.Turn(
            point.Properties.MouseWheelDelta,
            point.Position.X / ActualWidth,
            point.Position.Y / ActualHeight);

        e.Handled = true;
    }

    /// <summary>
    /// One frame of a zoom on its way: move the picture on hand, and say what is under the pointer
    /// now that the picture has moved beneath it.
    ///
    /// <para>The names follow the camera on the compositor, and fade as their shapes shrink round them,
    /// so nothing here moves them.</para>
    /// </summary>
    private void OnZoomMoved(object? sender, EventArgs e)
    {
        Place();

        // Not ReportWhatThePointerIsOver, which marks the shape out again whether or not it changed.
        // That is right once per new drawing and wrong at every frame of a move over the same one:
        // the outline is in the drawing's own pixels and moves with the placement, and redrawing it
        // is a pass over every shape and two new geometries, sixty times a second.
        FollowPointer();
    }

    /// <summary>
    /// Settle any folder still opening, whichever button went down, so what the press resolves
    /// against is the picture on screen rather than one on its way; and get ready to drag the picture
    /// with the left button, where the page allows it and the picture is zoomed.
    ///
    /// <para>Nothing moves yet, and the press is left for the framework to make a click of. Whether
    /// it is a drag is decided by how far it goes: see <see cref="MapDrag"/>.</para>
    /// </summary>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        _tapPress = point.PointerDeviceType != PointerDeviceType.Mouse || point.Properties.IsLeftButtonPressed;

        _descent.Finish();

        var movable = Zoomable
            && _drawing is { Viewport: not null }
            && !_zoom.Shown.IsWhole
            && point.Properties.IsLeftButtonPressed;

        _drag.Press(point.Position.X, point.Position.Y, movable);

        if (movable)
        {
            // A drag moves the picture out from under whatever it has not drawn yet.
            Underlay();
        }
    }

    /// <summary>
    /// Drag the picture with the left button held, once it has moved past what a click allows, and
    /// otherwise say what is under the pointer.
    ///
    /// <para>The pointer is captured for the drag, so a hand that leaves the map while dragging goes on
    /// moving the picture and its release is still heard.</para>
    /// </summary>
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var position = point.Position;
        var began = _drag.IsDragging;

        _pointer = position;

        if (ActualWidth > 0
            && ActualHeight > 0
            && _drag.Move(position.X, position.Y, point.Properties.IsLeftButtonPressed) is (var x, var y))
        {
            if (!began)
            {
                CapturePointer(e.Pointer);
                ProtectedCursor = _dragCursor;
            }

            _zoom.Drag(x / ActualWidth, y / ActualHeight);

            e.Handled = true;
            return;
        }

        FollowPointer();
    }

    /// <summary>The button came up, or the pointer was taken away: the drag is over and worth drawing.</summary>
    private void OnDragEnded(object sender, PointerRoutedEventArgs e)
    {
        if (EndDrag())
        {
            _zoom.Release();
        }
    }

    /// <summary>
    /// Stop dragging, and say whether there was a drag to stop. The picture stays where the drag
    /// left it, and the caller decides whether it is worth drawing there.
    /// </summary>
    private bool EndDrag()
    {
        // Released first, because letting go of the capture raises the capture-lost event that
        // brings the pointer back here.
        if (!_drag.Release())
        {
            return false;
        }

        ProtectedCursor = null;
        ReleasePointerCaptures();

        return true;
    }

    /// <summary>
    /// Say what is under the pointer, and mark it out, where that is not what was already under it.
    /// </summary>
    private void FollowPointer()
    {
        if (_pointer is not { } pointer)
        {
            return;
        }

        var hit = At(pointer);

        // Only when it changed. A pointer moves at the display's refresh rate and lands on the same
        // shape for most of that, so reporting every move would rebuild the same string sixty times
        // a second.
        if (hit == _hovered)
        {
            return;
        }

        _hovered = hit;

        ShowHovered();
        Report(hit);
    }

    /// <summary>Say what the pointer found.</summary>
    private void Report(ExploreHit? hit) => Hovered?.Invoke(this, hit);

    /// <summary>
    /// Redraw only when the scale actually moved. The root raises this for several reasons — the
    /// window changing host among them — and rasterising a full volume for each would be a repaint
    /// for something that did not change a pixel.
    /// </summary>
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(sender.RasterizationScale - _scale) > 0.001)
        {
            Redraw();
        }
    }

    /// <summary>
    /// The device was replaced, and every surface lost what it held: the picture on screen is not
    /// the one any drawing describes. Nothing is under the pointer, and nothing is kept, until the
    /// picture has been drawn again on the new device.
    /// </summary>
    private void OnDeviceReplaced(object? sender, EventArgs e)
    {
        _generation = _graphics.Generation;

        _descent.Finish();
        _redraws.Cancel();
        _departing.Renew();
        _pictures.Renew();

        _drawing = null;
        _hovered = null;
        _labels.Hide();
        _highlight.Clear();
        Report(null);

        Redraw();
    }

    /// <summary>
    /// Draw again for a new text size, because the bands and the label thresholds are sized for the
    /// old one. Raised off the UI thread, so it is sent back to it.
    /// </summary>
    private void OnTextScaleChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Redraw);

    /// <summary>
    /// Follow a change of accent colour. Raised off the UI thread, like the text size above.
    /// </summary>
    private void OnSystemColoursChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(TintHighlight);

    /// <summary>
    /// The lightest of the accent's shades, because it is drawn over a dark halo in either theme.
    /// </summary>
    private void TintHighlight() =>
        _highlight.TintHovered(SystemSettings.GetColorValue(UIColorType.AccentLight2));

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointer = null;
        _hovered = null;

        ShowHovered();
        Report(null);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_tapPress && !_drag.Dragged)
        {
            Pick(e.GetPosition(this));
        }
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(this);

        Pick(point);
        MenuRequested?.Invoke(this, point);
    }

    /// <summary>
    /// Say what is at <paramref name="point"/>. Neither block picks anything. The one standing in
    /// for items too small to draw is several thousand files at once, and §7.1 has no bulk action,
    /// and free space is not on the disk to be acted on.
    /// </summary>
    private void Pick(Point point)
    {
        // A click on nothing at all, with no picture yet, leaves the selection as it is. A click on a
        // picture still landing picks what has landed under it, as the readout already names.
        if (_drawing is null && _arriving is not { Landed.Count: > 0 })
        {
            return;
        }

        Picked?.Invoke(this, At(point) switch
        {
            { IsNode: true } hit => hit.Node,
            _ => null,
        });
    }

    /// <summary>
    /// Open what was double-clicked, and zoom to it where it cannot be opened.
    ///
    /// <para>A treemap draws what a folder holds inside the folder's own shape, so going into one is
    /// a zoom into that shape, and it is animated as one: the shape grows from where it was until it
    /// fills the map, and the folder's own drawing grows in over it. The page is what decides whether
    /// a node opens, and it says so by showing it here before <see cref="Activated"/> returns, so the
    /// shape is handed over for that moment and only for it. The root of a scan of a whole volume
    /// opens the same way, out of the volume drawn beside it.</para>
    ///
    /// <para>What does not open, a file, the block standing in for items too small to draw, or the
    /// block standing for free space, is zoomed to until it fills the map as far as its shape allows.
    /// For the block of small items that is how they come to be drawn one by one.</para>
    /// </summary>
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(this);

        if (!_tapPress || _drag.Dragged || Locate(point) is not { } spot || spot.Drawing.At(spot.X, spot.Y) is not { } hit)
        {
            return;
        }

        var drawing = spot.Drawing;

        // Only a treemap nests: an icicle draws what a shape holds below it rather than in it.
        var shape = drawing.Viewport is null ? null : ShapeAt(spot);

        // The shape the whole drawing is of opens only out of what is drawn beside it: the volume's
        // free space, beside the root of a treemap of a whole drive. Anywhere else it is already
        // open, and asking the page to open it again would be asking for the picture on screen.
        if (hit.IsNode && (hit.Node != _node || drawing.HasVolumeBeside))
        {
            // Counted rather than compared by node: the root opened out of its volume is the same
            // node drawn as another picture.
            var handedBefore = _picturesHanded;

            _opening = shape is { } opening ? (hit.Node, opening) : null;

            try
            {
                Activated?.Invoke(this, hit.Node);
            }
            finally
            {
                _opening = null;
            }

            if (_picturesHanded != handedBefore)
            {
                return;
            }
        }

        if (Zoomable && shape is { } frame)
        {
            var fitting = MapViewport.Fitting(_zoom.Shown.PictureOf(frame));

            // Already going there, which is a step to nowhere.
            if (fitting != _zoom.Target)
            {
                ZoomingIn?.Invoke(this, _zoom.Target);
            }

            Underlay();
            _zoom.GlideTo(fitting);
        }
    }

    /// <summary>
    /// One frame of a folder opening: the old picture stretched so the folder's shape fills more of
    /// the map, and the folder's own drawing over it on the same frame, coming in.
    /// </summary>
    private void OnDescentMoved(object? sender, EventArgs e)
    {
        _departing.Carry(_descent.Departing, 1, ActualWidth, ActualHeight);
        _pictures.Carry(_descent.Opened, _descent.Opacity, ActualWidth, ActualHeight);
    }

    /// <summary>
    /// The folder has opened: the old picture goes, and the names, the outlines and the readout are
    /// the new drawing's from here. Unless that drawing is still landing, in which case the old
    /// picture stays under it, where the opening left it, until it arrives.
    /// </summary>
    private void OnDescentArrived(object? sender, EventArgs e)
    {
        if (_drawing is null && _redraws.Pending is not null)
        {
            _openingOwed = true;
            return;
        }

        FinishOpening();
    }

    /// <summary>The end of a folder opening, once it has finished moving and its picture has arrived.</summary>
    private void FinishOpening()
    {
        _openingOwed = false;

        _departing.Clear();
        _departing.Carry(MapFrame.Whole, 1, ActualWidth, ActualHeight);

        _labels.Reveal();
        _highlight.Reveal();
        Place();
        ReportWhatThePointerIsOver();
    }
}
