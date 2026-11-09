using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Deguffer.App.Controls;

/// <summary>
/// Draws a scanned tree, and says what the pointer is over: the host the map's parts meet in.
///
/// <para>The geometry is painted into a picture rather than kept as shapes. A full volume lays out to
/// tens of thousands of them, and the framework's own performance guidance is that a vector element
/// repeated enough times should become an image instead — which is also what every reference
/// implementation does, WinDirStat rendering into a cached surface and blitting it rather than
/// keeping a shape per file. The picture is in the composition layer: each drawing a surface written
/// a region at a time, placed in the whole picture once, and the whole picture placed on the screen
/// by one camera (<see cref="MapCamera"/>), so a move writes the camera and nothing else.</para>
///
/// <para>Two things are not in the picture, and each has a part of its own that the camera moves
/// with it: the names, so they scale with the user's text size, and the lines round what is picked
/// and what the pointer is over, the dimming round a selection, the shape under the pointer lifted
/// and the hatch over what is being removed, so a click moves a line rather than rasterising a
/// volume again.</para>
///
/// <para>Nothing here knows what a drive is, how one is scanned, or how any of the views are laid
/// out. It is handed a tree, a node and a view, it asks Core for the matching
/// <see cref="ExploreSurface"/>, and it reports back what was pointed at (G1).</para>
///
/// <para>Each thing the map does is a part of its own: the redraws (<see cref="ExploreCanvas"/>), the
/// drawings kept (<see cref="ExploreLayers"/>), the camera and what moves it (<see cref="ExploreZoom"/>),
/// a change of folder (<see cref="ExploreFlight"/>), the shapes moving from one drawing to the next
/// (<see cref="ExploreMoves"/>), the names (<see cref="ExploreLabels"/>), what is marked out on the
/// picture (<see cref="ExploreMarking"/>, drawn by <see cref="ExploreHighlight"/>), the pointer
/// (<see cref="ExplorePointer"/>), the keyboard (<see cref="ExploreKeyboard"/>) and what a screen reader
/// reads (<see cref="MapReader"/>). Every rule they act on is in Core: what a new picture changes
/// (<see cref="PictureChange"/>), what a key does (<see cref="MapKeys"/>), a click from a drag
/// (<see cref="MapDrag"/>), what a click picks (<see cref="MapPick"/>) and a double-click opens
/// (<see cref="MapOpen"/>).</para>
///
/// <para>What is left here is where they meet: the constructor that builds and wires them, the API
/// the pages call, <see cref="Present"/>, which makes a new drawing the one every part works from, the
/// redraw and the steps of a change of folder, the lifecycle, the drag, the wheel and the routed
/// handlers. These stay together because each one joins several parts against the one picture on
/// screen, in an order that matters: a click, a key, a hover and a removal resolve against what the
/// screen shows (§7.1), so which part hears first of a new drawing, a flight landed or a zoom stopped
/// is decided in one place. Most of the length is the reasoning behind each ordering.</para>
///
/// <para>The budget for this file is 2,300 lines, which a test in Deguffer.Core.Tests
/// (<c>ExploreMapBudgetTests</c>) holds it to. Something new that would take it past that is a part
/// of its own, or moves one out first. Raising the budget is the maintainer's decision.</para>
/// </summary>
public sealed class ExploreMap : UserControl
{
    /// <summary>
    /// Lays out and paints each drawing of the picture, and says which is landing. See
    /// <see cref="ExploreCanvas"/>.
    /// </summary>
    private readonly ExploreCanvas _canvas;

    /// <summary>Whether a tap, a right-tap or a double-tap may act, from the presses that made it.</summary>
    private readonly MapPress _press = new();

    private readonly ExploreLabels _labels;

    private readonly ExploreHighlight _highlight;

    /// <summary>The whole picture in a corner, with the part on the screen marked, while the picture is zoomed in.</summary>
    private readonly ExploreOverview _overview;

    /// <summary>The device every drawing is written through. See <see cref="MapGraphics"/>.</summary>
    private readonly MapGraphics _graphics;

    /// <summary>Where the whole picture is on the screen: what each frame of a move writes for the picture to follow.</summary>
    private readonly MapCamera _camera;

    /// <summary>The map's composition tree: the two sets of drawings, then the outlines over them.</summary>
    private readonly ContainerVisual _root;

    /// <summary>
    /// How many times the device had been replaced when the drawings kept were written. See
    /// <see cref="CompositionDevice.Generation"/>.
    /// </summary>
    private int _generation;

    private readonly ExploreZoom _zoom;

    /// <summary>
    /// The two pictures of a change of folder, the picture worked from and the picture left, and the
    /// phase the change is in. See <see cref="ExploreFlight"/>.
    /// </summary>
    private readonly ExploreFlight _flight;

    /// <summary>
    /// The large shapes moving from the drawing on screen to the next as it arrives, and the move the
    /// redraw on its way owes. See <see cref="ExploreMoves"/>.
    /// </summary>
    private readonly ExploreMoves _moves;

    /// <summary>The pointer's look while it drags the picture. One for the life of the map (G5).</summary>
    private readonly InputCursor _dragCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    /// <summary>
    /// How many different pictures <see cref="Show(ISizedTree, int, ExploreView, Func{DateTime, ShapeColours}, Func{int, MapCaption}, MapLook, VolumeSpace, MapViewport?)"/>
    /// has been handed, so a double-click can tell whether the page opened what it asked for.
    /// </summary>
    private int _picturesHanded;

    /// <summary>How long each picture handed waits for its first drawing on screen, for a trace.</summary>
    private readonly PictureArrivals _arrivals = new(RenderingClock.Current);

    /// <summary>Whether the left or middle button is dragging the picture. See <see cref="MapDrag"/>.</summary>
    private readonly MapDrag _drag = new();

    /// <summary>
    /// What everything drawn is cut to while the picture is zoomed. A zoomed picture's shapes, their
    /// outlines and the names in their bands run off the control's edges, and a transform draws them
    /// over whatever is beside it unless something stops it. Kept and resized rather than replaced (G5).
    /// </summary>
    private readonly RectangleGeometry _edges = new();

    private readonly Grid _layers;

    /// <summary>The picks, the hatch, the card's light and the outlines round the pointer's and the keyboard's shapes.</summary>
    private readonly ExploreMarking _marking;

    /// <summary>What the map follows while it is loaded, undone when it is unloaded.</summary>
    private Subscriptions? _watching;

    /// <summary>
    /// The picture the map was last handed, which a change of folder may still be on its way to: the
    /// tree, the node of it, the view, and the volume drawn beside the node or
    /// <see cref="VolumeSpace.None"/>, as the page decides. See
    /// <see cref="ExploreSurface.Create(ISizedTree, int, ExploreView, int, int, double, double, ShapeColours, ExploreSpacing, VolumeSpace, MapViewport, MapStyle)"/>
    /// for where the volume is drawn.
    /// </summary>
    private MapPicture _shown = new(null, 0, ExploreView.Treemap, VolumeSpace.None);

    /// <summary>
    /// What the colours are to say, asked for at each repaint rather than held. See
    /// <see cref="ShapeColours.For(ExploreTree, ExploreColouring, ExploreScheme, DateTime, Deguffer.Core.Exploring.History.ScanGrowth, Deguffer.Core.Exploring.Files.DominantTypes)"/>: an age band is relative to the moment it is drawn.
    /// </summary>
    private Func<DateTime, ShapeColours> _colours = _ => ShapeColours.ByBranch(ExploreScheme.Standard);

    /// <summary>
    /// What to write on a shape. The tree knows its own names, and what a name means differs between
    /// a drive and a picture of memory, so the page that owns the tree says it.
    /// </summary>
    private Func<int, MapCaption> _captions = _ => MapCaption.None;

    /// <summary>How much room a treemap leaves round what each folder holds, and how every shape is finished.</summary>
    private MapLook _look = MapLook.Default;

    /// <summary>Where the pointer is, what it is over, and the readout and outline that follow it. See <see cref="ExplorePointer"/>.</summary>
    private readonly ExplorePointer _pointer;

    private ExploreSurface? _drawing;

    /// <summary>
    /// The part of the picture <see cref="_drawing"/> shows, which is the whole of it unless the
    /// drawing said otherwise. See <see cref="ExploreSurface.ShownAt"/>.
    /// </summary>
    private MapViewport _drawn;

    /// <summary>
    /// What the colours say in every drawing of the picture on screen, taken once per picture from
    /// <see cref="_colours"/>. A zoom that stops later draws in the same colours as the drawings kept
    /// under it, or an age band could change at the edge of one.
    /// </summary>
    private ShapeColours? _shapeColours;

    /// <summary>
    /// The high contrast theme's colours the drawings of the picture on screen are in, or null where
    /// they are coloured and shaded: read with <see cref="_shapeColours"/>, so the ground under them
    /// agrees with them, and read again to tell whether a change of the system's colours changed them.
    /// </summary>
    private ContrastColours? _contrast;

    /// <summary>
    /// The finest tile shown over the drawing worked from at a point of the picture, asked of whichever
    /// set of layers is the picture on screen at the time. One for the life of the map, because the
    /// pointer asks at the display's rate (G5).
    /// </summary>
    private readonly Func<double, double, MapTile?> _tileOver;

    /// <summary>
    /// How a drawing is read in the tree the page handed over last, for <see cref="MapHitTest"/>. One
    /// for the life of the map, because the pointer asks at the display's rate (G5). The marking's own
    /// (<see cref="ExploreMarking.ReadingOf"/>), so an outline marks the shape a click there picks.
    /// </summary>
    private readonly Func<ISizedTree, MapReading?> _readingOf;

    /// <summary>The keyboard on the shapes, and which shape it is on. See <see cref="ExploreKeyboard"/>.</summary>
    private readonly ExploreKeyboard _keyboard;

    /// <summary>The reader of the drawing a screen reader last read. See <see cref="Reader"/>.</summary>
    private MapReader? _reader;

    public ExploreMap()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

        _graphics = MapGraphics.For(compositor);
        _generation = _graphics.Device.Generation;
        _camera = new MapCamera(compositor);
        _zoom = new ExploreZoom(ElementCompositionPreview.GetElementVisual(this), _camera, SystemMotion.Current, DispatcherQueue);
        var owner = new DispatcherQueueSynchronizationContext(DispatcherQueue);

        var pictures = new ExploreLayers(compositor, _graphics, _camera, SystemMotion.Current, owner);
        var departing = new ExploreLayers(compositor, _graphics, _camera, SystemMotion.Current, owner);
        _highlight = new ExploreHighlight(compositor, _graphics, _camera);
        _marking = new ExploreMarking(_highlight);
        _readingOf = _marking.ReadingOf;
        _keyboard = new ExploreKeyboard(_marking.Picked, node => Reader?.FolderOf(node));
        _moves = new ExploreMoves(compositor, _graphics.Geometry, _camera, SystemMotion.Current);
        _labels = new ExploreLabels(_camera, pictures.Root, _graphics.Device, SystemMotion.Current);
        _pointer = new ExplorePointer(Locate, Shine, ShowHovered, Report);
        _overview = new ExploreOverview(_camera, _zoom, SystemMotion.Current, () => _pointer.Position);

        pictures.Follow();

        // The outlines go over the picture, and the labels over both. A label is inset from its
        // shape's edge and an outline runs along it, so the two rarely meet — and where they do, the
        // name of the thing is worth more than the last pixel of the line round it. The picture a
        // change of folder is leaving is stacked against the new one as each step starts (see Swap).
        _root = compositor.CreateContainerVisual();
        _flight = new ExploreFlight(_root, pictures, departing, this);
        _tileOver = (x, y) => _flight.Pictures.TileOver(x, y);
        _canvas = new ExploreCanvas(this, _graphics, _zoom, _flight, owner, () => _pointer.Position, Present, () => Painting, Redraw);
        _root.Children.InsertAtTop(_moves.Root);
        _root.Children.InsertAtTop(_highlight.Root);

        _layers = Stack();
        Content = _layers;

        FollowCamera();
        FollowPictures(pictures, departing);
        FollowInput();
        FollowControl();

        // One tab stop, inside which the arrow keys move between the shapes, as they move between the
        // items of a list. A screen reader is given the shapes as a tree of their own (see
        // MapAutomationPeer), with the one the keyboard is on as the focus.
        IsTabStop = true;
        AutomationProperties.SetName(this, "Map of what was scanned");

        // Naming where the same content is readable as well, because a list is still the quicker way
        // to read a great many names.
        DescribeControls();
    }

    /// <summary>
    /// The map's layers, bottom to top: the composition tree, then the names, then the overview.
    ///
    /// <para>The composition tree is hosted under an element of its own, so the names, which are XAML,
    /// can go over it: a hosted visual is drawn over its host's children. The ground is transparent
    /// rather than absent, so the whole control takes the pointer. The picture itself takes none of it,
    /// so what is under the pointer is always this control's own answer rather than whichever drawing
    /// happens to be topmost there.</para>
    /// </summary>
    private Grid Stack()
    {
        var picture = new Border();
        ElementCompositionPreview.SetElementChildVisual(picture, _root);

        return new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children = { picture, _labels, _overview },
        };
    }

    /// <summary>Answer the camera on its way, at rest and retargeted, and the overview that moves it.</summary>
    private void FollowCamera()
    {
        _zoom.Moved += OnZoomMoved;
        _zoom.Arrived += (_, _) =>
        {
            OnZoomArrived();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        };
        _zoom.Retargeted += (_, _) =>
        {
            ShowOverview();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        };

        // The camera is held where it was, measured from its new origin, until this returns, so
        // everything on it is placed from that origin at once (see ExploreOrigin.Move).
        _zoom.OriginMoved += (_, _) => Fit();

        // A pinch, a touch or the wheel with Ctrl held moves the picture on the compositor before the
        // map hears of it, so a change of folder still flying is settled and the whole picture painted
        // under the zoomed one as soon as it does, as they are for a move the map starts itself.
        _zoom.Started += (_, _) => PrepareMove();

        // A press on the overview moves the camera as any other input does, from a settled screen
        // with the whole picture under the zoomed one.
        _overview.Moving += (_, _) => PrepareMove();
        _overview.Entered += (_, _) => _pointer.Follow();
        _overview.HiddenChanged += (_, _) => OverviewHiddenChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Answer the two sets of drawings, <paramref name="pictures"/> and <paramref name="departing"/>,
    /// a change of folder's flight between them, and shapes moving from one drawing to the next.
    /// </summary>
    private void FollowPictures(ExploreLayers pictures, ExploreLayers departing)
    {
        // Both sets, as the two change places with each change of folder that flies (see Swap). The
        // overview is of the picture the map works from: the set a change of folder leaves keeps its
        // own, for a change turned round.
        pictures.OverviewChanged += OnOverviewChanged;
        departing.OverviewChanged += OnOverviewChanged;
        pictures.Faded += OnFaded;
        departing.Faded += OnFaded;
        pictures.TileAnswersChanged += OnTileAnswersChanged;
        departing.TileAnswersChanged += OnTileAnswersChanged;

        _flight.Descent.Midway += OnDescentMidway;
        _flight.Descent.Arrived += OnDescentArrived;
        _moves.Started += OnMoveStarted;
        _moves.Landed += OnMoveLanded;
    }

    /// <summary>Answer the pointer, touch, the keys and the focus, each against what the screen shows (§7.1).</summary>
    private void FollowInput()
    {
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnDragEnded;
        PointerCaptureLost += OnDragEnded;
        PointerExited += (_, _) => _pointer.Leave();
        PointerWheelChanged += OnPointerWheelChanged;
        DoubleTapped += OnDoubleTapped;
        KeyDown += OnKeyDown;
        PreviewKeyDown += OnPreviewKeyDown;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        ContextRequested += OnContextRequested;
        _keyboard.Landed += OnKeyboardLanded;

        // Picking is separate from opening, on the file-manager idiom: one click says which one, two
        // says go in. A right-click picks as well, so the menu that follows is about the shape under
        // the pointer rather than about whatever was picked last.
        Tapped += OnTapped;
        RightTapped += OnRightTapped;
    }

    /// <summary>Answer the control's own life on the page: its size, its theme, and being loaded and unloaded.</summary>
    private void FollowControl()
    {
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnThemeChanged;
    }

    /// <summary>
    /// Follow what the map follows while it is loaded (<see cref="Watch"/>), take the system's look and
    /// motion as they are now, and draw what was owed while the map was away (<see cref="CatchUp"/>).
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _watching?.Dispose();
        _watching = Watch();

        TintHighlight();
        _zoom.FollowMotion();
        CatchUp();
    }

    /// <summary>
    /// What the map follows while it is loaded: the system's look and motion, the graphics device, and
    /// the root it was loaded into.
    ///
    /// <para>Dragged to a differently scaled display, the control's size in device-independent units
    /// does not change, so <see cref="FrameworkElement.SizeChanged"/> does not fire and the canvas would
    /// stay at the old resolution. The scale is read from the <see cref="XamlRoot"/> rather than from
    /// this element: the identically documented property on <see cref="UIElement"/> does not update on
    /// a scale change (microsoft-ui-xaml #9610), and the root is not attached until this is loaded.
    /// The root the map was loaded into is the one it leaves, though it may be gone from the control by
    /// the time <see cref="FrameworkElement.Unloaded"/> is raised.</para>
    /// </summary>
    private Subscriptions Watch()
    {
        var watching = new Subscriptions()
            .Add(() => SystemLook.Current.Changed += OnLookChanged, () => SystemLook.Current.Changed -= OnLookChanged)
            .Add(() => SystemMotion.Current.Changed += OnMotionChanged, () => SystemMotion.Current.Changed -= OnMotionChanged)
            .Add(() => _graphics.Device.Replaced += OnDeviceReplaced, () => _graphics.Device.Replaced -= OnDeviceReplaced);

        if (XamlRoot is { } root)
        {
            watching.Add(() => root.Changed += OnRootChanged, () => root.Changed -= OnRootChanged);
        }

        return watching;
    }

    /// <summary>
    /// Draw what the map was owed while it was away, which nothing else will ask for once it is back.
    ///
    /// <para>A resize that arrived while this was on screen, and was still waiting to be drawn when the
    /// page was navigated away from, was dropped rather than rasterised for a page nobody is looking at.
    /// Coming back at that same size raises no <see cref="FrameworkElement.SizeChanged"/>, so without
    /// this the map stays stretched over the old canvas for as long as the page is open.</para>
    ///
    /// <para>The same goes for a zoom that was still moving when the page was left. It was finished
    /// where it was going rather than eased for nobody, so the drawing is of where it was, and the
    /// drawings kept of the rest of the picture are still good for the one it went to. A redraw stopped
    /// as the page was left is owed in full, because the picture it was drawing has not been on screen,
    /// and so is everything where the device was replaced while the map was away: nothing it kept is on
    /// its surfaces any more.</para>
    /// </summary>
    private void CatchUp()
    {
        if (_generation != _graphics.Device.Generation)
        {
            OnDeviceReplaced(_graphics, EventArgs.Empty);
            return;
        }

        if (_canvas.IsOwed)
        {
            Redraw();
        }
        else if (_drawing is { } drawing)
        {
            if (!_canvas.Fits(drawing))
            {
                Redraw();
            }
            else if (_drawn != _zoom.Shown)
            {
                OnZoomArrived();
            }
        }
    }

    /// <summary>
    /// Stop everything on its way, and let go of what a page kept alive while it is away
    /// (NavigationCacheMode) need not hold: the map comes back to the drawing on screen, and
    /// <see cref="CatchUp"/> draws whatever was stopped.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A pending redraw for a size this control is no longer showing at. Left running it
        // would rasterise a whole volume for a page that has been navigated away from. A change of
        // folder still flying, or a drag still held, is finished where it was going for the same reason.
        _canvas.CancelResize();
        _zoom.Stop();
        SettleStep();
        EndDrag();

        // A redraw still landing is stopped for the same reason, and owed when the page is back.
        _canvas.Leave();

        // At 4K, 33 MB for each drawing kept, as for the paint buffer the canvas let go. Only the
        // drawing on screen is needed to come back to, and the rest is asked for again when the
        // picture next moves.
        _flight.Pictures.Light(null, null);
        _flight.Pictures.Trim();
        _flight.Departing.ReleaseTiles();
        _graphics.Device.Trim();

        // The labels go back with it. Dropping the redraw drops the thing that would have put
        // them back, and Loaded only redraws where the size has actually moved — so without
        // this a map returned to at the size it was last drawn at comes back with no names on
        // it at all, and nothing on the page would put them there.
        _labels.Reveal();

        // A map moved to a new parent can be loaded there before it is unloaded from the old one,
        // and one still loaded keeps following what it was following.
        if (!IsLoaded)
        {
            _watching?.Dispose();
            _watching = null;
        }
    }

    /// <summary>
    /// Draw the picture again on a new ground for the new theme.
    ///
    /// <para>The ground is baked into the canvas, so unlike every themed control around it the map
    /// cannot restyle itself. Nothing else here fires on a theme switch — the page is kept alive by
    /// NavigationCacheMode, so a trip to Settings and back does not rebuild it either — and the map
    /// would keep the old ground until the window was resized. WindowBackdrop subscribes this same
    /// event for the same reason. High contrast turned on or off raises it too, and it can arrive
    /// before Windows' own colour event, so the system's look is read again before the redraw takes
    /// its colours from it.</para>
    /// </summary>
    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        SystemLook.Current.Refresh();
        Redraw();
    }

    /// <summary>
    /// The node the user asked to open, by double-clicking a shape. A page that opens it shows it here
    /// before this returns, and one that does not has the map zoom to it instead. See
    /// <see cref="OnDoubleTapped"/>.
    /// </summary>
    public event EventHandler<int>? Activated;

    /// <summary>
    /// The reader pressed Backspace on the map, which goes up to the folder that holds the one on
    /// screen. The page goes, as it decides what is above.
    /// </summary>
    public event EventHandler? Ascending;

    /// <summary>
    /// What the page knows of a node of a tree it handed over that a screen reader is told and the map
    /// does not: see <see cref="MapShapeFacts"/>. Null from the page for a tree it has nothing to say of,
    /// such as a step of a replay, and then a screen reader is given no shapes on it, only the map.
    /// </summary>
    public Func<ISizedTree, int, MapShapeFacts?>? Facts { get; set; }

    /// <summary>
    /// Whether a node of a tree the page handed over may be removed, and if not why, which a screen
    /// reader hears with what can be done with the shape. Asked only for that, because the page asks
    /// the machine for it (see <see cref="MapShapeText.Help(MapShapeFacts, ExploreVerdict?)"/>). Null
    /// from a page that removes nothing, and for a tree it has nothing to say of, and then nothing is
    /// said either way.
    /// </summary>
    public Func<ISizedTree, int, ExploreVerdict?>? Removal { get; set; }

    /// <summary>
    /// Whether the picture is drawn in a high contrast theme's two colours, so its colours say nothing
    /// of what the page coloured it by. See <see cref="ContrastColours"/>.
    /// </summary>
    public bool Contrasted => _contrast is not null;

    /// <summary><see cref="Contrasted"/> has a new answer.</summary>
    public event EventHandler? ContrastChanged;

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
    /// Whether the wheel, the touchpad, touch, a drag and the keyboard move the picture, where the
    /// drawing can be zoomed at all. Off unless the page asks. A new tree of the same place keeps the
    /// zoom, and the camera follows what the reader zoomed into (<see cref="Follow"/>), so a page that
    /// draws a new reading every few seconds can offer it too.
    /// </summary>
    public bool Zoomable
    {
        get;
        set
        {
            field = value;
            DescribeControls();
            Unlock();
        }
    }

    /// <summary>
    /// Let the camera move where the page allows it and the drawing worked from can be zoomed, and
    /// hand the wheel, the touchpad and touch back to the page everywhere else.
    /// </summary>
    private void Unlock()
    {
        _zoom.Movable = Zoomable && _drawing is { Zoom: not null };
        ShowOverview();
    }

    /// <summary>
    /// Whether the reader hid the overview the zoomed map shows in a corner. The page keeps the
    /// choice, and hears of the reader changing it through <see cref="OverviewHiddenChanged"/>.
    /// </summary>
    public bool OverviewHidden
    {
        get => _overview.Hidden;
        set => _overview.Hidden = value;
    }

    /// <summary>The reader hid the overview, or brought it back, on the overview itself.</summary>
    public event EventHandler? OverviewHiddenChanged;

    /// <summary>
    /// Want the overview on screen while the picture can be moved and is zoomed in, or on its way in
    /// or out, and with no change of folder flying: it is of the picture on screen, and that is on its
    /// way to being another one.
    /// </summary>
    private void ShowOverview() =>
        _overview.Want(_zoom.Movable && !Stepping && !(_zoom.Shown.IsWhole && _zoom.Target.IsWhole));

    /// <summary>The overview of the picture the map works from has a new answer.</summary>
    private void OnOverviewChanged(object? sender, EventArgs e)
    {
        if (sender == _flight.Pictures)
        {
            _overview.ShowWhole(_flight.Pictures.Overview);
        }
    }

    /// <summary>
    /// What the picture is, where the same contents can be read instead, and what the page does with a
    /// shape opened or a step back: the page's to say, as the pages differ in all three. The map adds
    /// how the pointer and the keys move the picture, where they do.
    /// </summary>
    public string Description
    {
        get;
        set
        {
            field = value;
            DescribeControls();
        }
    } = "A picture of what is using the space. Choose List in the View box for the same contents as a readable list.";

    /// <summary>
    /// Say what the map is and how the pointer moves it, for a screen reader on the focused map:
    /// the caption naming the same on the page is only reached by browsing to it.
    /// </summary>
    private void DescribeControls() =>
        AutomationProperties.SetHelpText(
            this,
            Description
            + " The arrow keys move between the shapes, and pick the one they reach. Enter opens it, "
            + "Backspace goes up a folder, and the menu key or Shift+F10 opens its menu."
            + (Zoomable
                ? " On the treemap, turn the mouse wheel or pinch to zoom, and hold Shift with the wheel "
                    + "to move across. Drag with the left or middle button, two fingers or a touch to move a "
                    + "zoomed picture, and double-click anything else to zoom to it. Plus and minus zoom "
                    + "about the middle, the arrow keys with Shift move the picture, and Home shows all of it. "
                    + "The picture follows the arrow keys to a shape off the screen or too small to see. While the "
                    + "picture is zoomed in, an overview in a corner shows all of it with the part on the screen "
                    + "marked: drag the mark to move the picture, or click anywhere in the overview to go there. "
                    + "The keys above move the picture as the overview does, and Tab reaches its button to hide "
                    + "it or bring it back."
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
    /// <param name="look">How much room a treemap leaves round each folder, and how every shape is finished.</param>
    /// <param name="growth">What grew since the last scan, for a map coloured by growth, or null.</param>
    /// <param name="types">The kind of file each node holds most of, for a map coloured by type, or null.</param>
    /// <param name="viewport">See the other overload.</param>
    public void Show(
        ExploreTree? tree,
        int node,
        ExploreView view,
        ExploreColouring colouring,
        ExploreScheme scheme,
        MapLook look,
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
            tree is null ? _ => MapCaption.None : Captions(tree),
            look,
            volume,
            viewport);

    /// <summary>What a shape of a scanned <paramref name="tree"/> is labelled with: its name, its size and when it was written.</summary>
    public static Func<int, MapCaption> Captions(ExploreTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return node => MapShapeText.Caption(tree, node);
    }

    /// <summary>
    /// Draw <paramref name="node"/> of any tree a layout can lay out.
    ///
    /// <para>On a treemap of the same tree, a change of folder is a camera move: into a folder below
    /// this one, the camera flies into its shape and the folder's own picture comes in as it arrives;
    /// out to a folder above it, however far up, the picture of that folder comes in round this one
    /// and the camera pulls back until it fills the map, so the folder left is seen shrinking into its
    /// place (<see cref="FolderSteps"/>, <see cref="MapDescent"/>). To a folder where neither holds
    /// the other, the camera pulls back to the folder that holds both and flies on into the other, as
    /// one move (<see cref="MapPassage"/>). Asked for the folder it left before the flight arrives, the
    /// camera turns round where it is. An icicle and a sunburst have no camera, and change at once.</para>
    ///
    /// <para>The same place in another of the views that draw a picture changes by carrying its largest
    /// shapes from where the one draws them to where the other does (<see cref="ViewMorph"/>).</para>
    /// </summary>
    /// <param name="colours">What the colours are to say, asked at each repaint.</param>
    /// <param name="captions">What to write on a shape of the tree this drawing chose to label.</param>
    /// <param name="look">
    /// How much room a treemap leaves round what each folder holds, and how every shape is finished.
    /// Its schemes are not read: the colours already say what they are drawn in.
    /// </param>
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
        Func<int, MapCaption> captions,
        MapLook look,
        VolumeSpace volume,
        MapViewport? viewport = null)
    {
        ArgumentNullException.ThrowIfNull(colours);
        ArgumentNullException.ThrowIfNull(captions);
        ArgumentNullException.ThrowIfNull(look);

        var was = _zoom.Shown;
        var change = PictureChange.Between(_shown, new MapPicture(tree, node, view, volume), viewport, Zoomable);

        MapFrame? into = null;

        if (change.Another)
        {
            _picturesHanded++;
            _arrivals.Handed(change.Asked);

            // The camera's drag ends with the map's, where it was held, and the new picture is drawn there.
            if (EndDrag())
            {
                _zoom.Stop();
            }

            if (_flight.State.Left is { Drawing: { } drawing } left && change.Returns(left.Picture, left.Viewport) && Stepping)
            {
                Return(drawing, colours, captions, look);
                return;
            }

            // Any other change of folder lands the one on its way first: the screen has room for the
            // flight between two pictures, and this one starts from the picture it arrived at.
            LandStep();

            var step = change.Step(_drawing is { Zoom: not null });

            // A flight through a folder starts as a step out to it, drawn whole as any folder opened is.
            if (!change.SamePlace)
            {
                _zoom.Reset(step.Meeting is null ? change.Opened : MapViewport.Whole);
            }

            // The picture on screen stays, to fly from, and the new one is drawn into the other set
            // of layers: over it for a step in, which the new picture grows out of, and under it for a
            // step out, which it shrinks back into.
            // Only a drawing placed by the zoom has a step other than across (PictureChange.Step).
            if (step.Way != FolderStep.Across && _drawing is { } leaving)
            {
                into = step.Way == FolderStep.Into
                    ? leaving.ScreenOf(node, was, change.Opened) ?? MapFrame.Whole
                    : null;

                _flight.State = FolderFlight.Begin(step, new FolderLeft(_shown, was, leaving, _shapeColours), change.Opened);

                Swap(was);

                // The drawing on screen is the one being left now, carried away with the old
                // picture, so nothing is resolved against it again. The pointer is over nothing
                // until the flight has arrived.
                _drawing = null;
            }
        }

        // The overview of the place left goes with it, rather than standing for this one until its
        // whole drawing arrives. A change of folder has drawn into the other set, which has none. The
        // same place laid out again keeps it until its own replaces it, as it keeps the zoom.
        if (change.Another && !change.SamePlace)
        {
            _flight.Pictures.ForgetOverview();
        }

        if (change.Another)
        {
            CarryFocus(change);
        }

        Hold(change.Asked);
        _colours = colours;
        _captions = captions;
        _look = look;

        // A step in starts before the new picture is drawn, so the drawing arrives into a flight
        // already on its way: its outlines wait for it the way they wait for a zoom, its names ride it in, and
        // the pointer is over nothing until the picture it is over is the one on screen. The names
        // of the picture left go with it. A step out flies once the picture it goes out to is drawn.
        if (_flight.State.IsUnderWay && change.Another)
        {
            _labels.Hide();
            _highlight.Hide();

            if (into is { } shape)
            {
                _flight.Descent.Start(shape, FolderStep.Into);
            }
        }

        if (change.SamePlace)
        {
            Shift();
        }
        else if (change.Morph)
        {
            Morph();
        }
        else
        {
            Redraw();
        }

        // Nothing to fly to after all: a folder with nothing in it to draw.
        if (_flight.State.IsUnderWay && _drawing is null && _canvas.Pending is null)
        {
            LandStep();
        }

        // What the pointer is over and what is picked were read in the tree before, and are read in
        // this one now. The drawing on screen stays until the new one has arrived, and answers through
        // the counterparts of the shapes it draws where it is of the same place (MapReading); a change
        // of folder has none on screen, and the pointer is over nothing until it lands.
        if (change.Another)
        {
            Remark();
            _pointer.Reread();
        }

        // The same picture, zoomed to where the reader was, moves there as a zoom to a shape does.
        // After the redraw, which draws where the zoom is now and is the drawing the move starts from.
        if (change.Glide is { } returning)
        {
            _zoom.GlideTo(returning);
        }

        ShowOverview();
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

        if (!_marking.Select(nodes, _drawing))
        {
            return;
        }

        // What the pointer is over may have just become what is picked, in which case it stops
        // being outlined separately. See ShowHovered.
        ShowHovered();
    }

    /// <summary>
    /// Mark <paramref name="nodes"/> of <paramref name="tree"/> out on the picture as what a removal
    /// under way is acting on, and nothing once it is over.
    ///
    /// <para>Told, as the selection is, because what is being removed is the removal's to say: it is
    /// what was picked when the removal was asked for, which a pick made since does not change.</para>
    /// </summary>
    public void MarkRemoving(ISizedTree? tree, IReadOnlyList<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        _marking.MarkRemoving(tree, nodes, _drawing, _canvas.Scale);
    }

    /// <summary>
    /// Light what a card beside the map points at, dimming the rest, or nothing where
    /// <paramref name="light"/> is null. See <see cref="MapLight"/>.
    ///
    /// <para>Told, as the selection is, because what the card points at is the card's to say. Kept and
    /// lit again on every new drawing, so it stays on the shapes it names through a zoom, a resize or a
    /// snapshot, as long as the page keeps it.</para>
    /// </summary>
    public void Light(MapLight? light) => _marking.Light(light, _drawing);

    /// <summary>
    /// Wait for the drag to stop before redrawing, on the terms
    /// <see cref="ExploreCanvas.AwaitResize"/> gives.
    ///
    /// <para>The first size the control is ever given is drawn at once. There is no drawing to
    /// stretch in the meantime, so deferring that one would leave the panel empty for the length of
    /// the wait every time the page is opened.</para>
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _zoom.Resize(ActualWidth, ActualHeight);
        _overview.Fit(ActualWidth, ActualHeight);

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

        _canvas.AwaitResize();
    }

    /// <summary>
    /// Where <paramref name="drawing"/>'s canvas lies in the picture, drawn at <see cref="_drawn"/> and
    /// shown on a map of the control's size from the camera's origin now.
    /// </summary>
    private MapTransform Placed(ExploreSurface drawing) =>
        _drawn.Canvas(drawing.Width, drawing.Height, ActualWidth, ActualHeight, _zoom.Origin);

    /// <summary>
    /// Cut the picture at the map's edges while anything runs past them. The camera itself follows the
    /// tracker on the compositor (see <see cref="ExploreZoom"/>), so nothing here moves the picture.
    /// </summary>
    private void Place()
    {
        // Cut only while something can run past the edges. Unzoomed, nothing does except the halo of
        // an outline round a shape at the edge, which has always been drawn whole.
        var cut = !(_zoom.Shown.IsWhole && _drawn.IsWhole && !_flight.State.IsUnderWay);

        if (cut)
        {
            _edges.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        }

        _layers.Clip = cut ? _edges : null;
    }

    /// <summary>
    /// Put each drawing, and the outlines and the names of the one worked from, where they lie in the
    /// picture at the control's size now. For a new size, a new drawing or a new origin: a move changes
    /// only the camera.
    /// </summary>
    private void Fit()
    {
        _flight.Pictures.Fit(ActualWidth, ActualHeight, _zoom.Origin);

        if (_drawing is not { } drawing)
        {
            return;
        }

        _highlight.PlaceOver(Placed(drawing), ActualWidth, ActualHeight, _zoom.Origin);

        _labels.Place(_drawn.Labels(drawing.Width, drawing.Height, ActualWidth, ActualHeight, _canvas.Scale, _zoom.Origin), ActualWidth, ActualHeight);
    }

    /// <summary>
    /// Draw the picture on screen again in a new look: <paramref name="colours"/> and
    /// <paramref name="look"/>'s spacing and finish, for the same tree, folder and view. The old look
    /// fades into the new once the new drawing has landed, and shapes a new spacing or a band shown or
    /// hidden moved travel to their new places
    /// (<see cref="Shift"/>), so the difference a choice makes is seen as it is made, rather than the
    /// new look replacing the old a region at a time.
    /// </summary>
    public void Restyle(Func<DateTime, ShapeColours> colours, MapLook look)
    {
        ArgumentNullException.ThrowIfNull(colours);
        ArgumentNullException.ThrowIfNull(look);

        _colours = colours;
        _look = look;

        Shift();
    }

    /// <summary>
    /// Draw the same place laid out again, and move from the drawing on screen to it as it arrives:
    /// the large shapes travel and the rest crossfades over the same time (<see cref="MotionToken.Shift"/>).
    /// The crossfade keeps the earlier drawing until it is over, which the shapes that went show as
    /// they shrink away.
    /// </summary>
    private void Shift()
    {
        var motion = SystemMotion.Current.For(MotionToken.Shift);

        Redraw(motion);
        _moves.Owe(MapMove.Shift, motion, _canvas.Pending is not null);
    }

    /// <summary>
    /// Draw the same place in another view, and carry its largest shapes into it from the drawing on
    /// screen as it arrives, while the rest crossfades over the same time (<see cref="MotionToken.Morph"/>).
    /// With motion off the views swap at once.
    /// </summary>
    private void Morph()
    {
        var motion = SystemMotion.Current.For(MotionToken.Morph);

        Redraw(motion);
        _moves.Owe(MapMove.Morph, motion, _canvas.Pending is not null);
    }

    private void Redraw() => Redraw(Motion.Instant);

    /// <param name="fade">How the new drawing comes in over the old. See <see cref="ExploreLayers.Replacement"/>.</param>
    private void Redraw(Motion fade)
    {
        // Whatever brought us here is more current than a size change still waiting to be drawn, or a
        // redraw stopped when the page was left; and a redraw for any other reason replaces a move
        // from the drawing on screen that was owed.
        _canvas.Supersede();
        _moves.Forgo();

        // Nothing can see it, and the page asks again as it brings the map back. ExplorePage
        // collapses this for the List view and calls Show() in the same breath, so without this a
        // switch to List rasterises a whole volume into a control nobody is looking at.
        //
        // What was drawn is dropped rather than kept, because the page brings the map back with a
        // Show() that draws it again anyway, and the zoomed drawings are 33 MB each at 4K.
        if (_canvas.IsCollapsed)
        {
            _canvas.Drop();
            Forget();
            return;
        }

        _canvas.Rescale();

        if (_shown.Tree is not { } tree || !_canvas.HasPixels || tree.SizeOf(DrawnNode) <= 0)
        {
            // Every drawing goes, and the buffer they were painted through: a map with nothing to
            // show holds no memory for it.
            _canvas.Empty();
            Forget();
            _shapeColours = null;
            _labels.Clear();
            _highlight.Clear();
            Unlock();

            // There is no picture now, so nothing is under the pointer. Said rather than left: a
            // cancelled scan takes the tree away, and without this the line under the map goes on
            // naming whatever was last hovered over a blank canvas.
            Report(null);

            // A folder opened into nothing: the picture it opened out of goes too.
            if (_flight.State.Owed)
            {
                FinishStep();
            }

            return;
        }

        // The clock is read here rather than held, because the age bands are relative to now and
        // a map left on screen overnight would otherwise keep yesterday's answer. A repaint costs
        // one read of it against a full rasterisation.
        //
        // In a high contrast theme the colours say nothing but where each shape's edges are: see
        // ContrastColours.
        var wasContrasted = Contrasted;

        _contrast = SystemLook.Current.State.Contrast?.ToMap();

        var colours = _contrast is { } contrast ? ShapeColours.Contrasted(contrast) : _colours(DateTime.UtcNow);

        _shapeColours = colours;

        if (Contrasted != wasContrasted)
        {
            ContrastChanged?.Invoke(this, EventArgs.Empty);
        }

        // Every drawing kept is of the picture as it was, so none of them is shown again, though
        // they stay on screen until the new one has landed over them. Drawn at the zoom on screen
        // now, which is partway through a move if one is on its way: a repaint mid-move is placed
        // for the rest of the move like any other drawing.
        _flight.Pictures.Forget();

        Request(new ExplorePainting(tree, Drawn, colours, _look, Ground()), _zoom.Shown, fade);
    }

    /// <summary>
    /// Work from no drawing, with nothing under the pointer, for a picture on screen that no drawing
    /// describes any more: a map collapsed, left with nothing to draw, or on a device replaced. One
    /// place, so a field added for what the pointer resolves against is forgotten with the rest (§7.1).
    /// </summary>
    private void Forget()
    {
        _drawing = null;
        _pointer.Forget();
    }

    /// <summary>
    /// Show the picture where a zoom or a drag stopped: the drawing kept of that part of it, where
    /// there is one, and a new one otherwise. The picture has not changed, so every drawing already
    /// made of it still stands.
    /// </summary>
    private void OnZoomArrived()
    {
        // The drawing of where it stopped is on its way, and wants the cores the tiles would take. The
        // tiles already under way still arrive, and show until it has.
        _flight.Pictures.RestTiles();

        // Left for Loaded, which shows the arrival when the map is back, as it does a redraw.
        if (!_canvas.IsShowing || _drawing is not { } drawing)
        {
            return;
        }

        // A resize still settling has made every drawing kept the wrong size, and its names would
        // go back over a stretched picture at the old size's positions. The whole picture is drawn
        // again at the new one instead, which is what the settling was waiting to do.
        if (_canvas.IsSettling || !_canvas.Fits(drawing))
        {
            Redraw();
            return;
        }

        // Nothing new to draw, and the names are still where this drawing put them. Unless a redraw
        // is landing, which was asked for somewhere the zoom has since left, or of a picture that
        // has changed since this drawing was made.
        if (ShowsTheDrawingAsDrawn)
        {
            _flight.Pictures.HideTiles();
            _labels.Reveal();
            Place();
            _pointer.Reread();
            return;
        }

        Request(Painting, _zoom.Shown, Motion.Instant);
    }

    /// <summary>
    /// Show <paramref name="viewport"/> of <paramref name="painting"/>: the drawing kept of it, at
    /// once, where there is one, and otherwise a redraw of it, which lands over the picture on screen.
    /// </summary>
    /// <param name="fade">How a new drawing comes in over the one on screen. See <see cref="ExploreLayers.Replacement"/>.</param>
    private void Request(ExplorePainting painting, MapViewport viewport, Motion fade)
    {
        if (_canvas.Kept(viewport) is { } kept)
        {
            Present(kept);
            return;
        }

        // The screen is put where the zoom now says before anything lands on it. A zoom can change
        // with nothing moving the drawings, as a new picture resets it and a page left mid-glide
        // finishes it, and every point is resolved through the zoom (§7.1): until the new drawing
        // arrives, the old one answers, and it has to be shown where it is resolved.
        Place();

        _canvas.Draw(painting, viewport, fade);
    }

    /// <summary>
    /// What the drawing the map works from was drawn from, as a drawing of more of the same picture is:
    /// the tree, the folder drawn, and the colours and the look the last redraw took. Asked only while
    /// there is a drawing, which a redraw makes only of a tree, in colours.
    /// </summary>
    private ExplorePainting Painting =>
        _shown.Tree is { } tree && _shapeColours is { } colours
            ? new ExplorePainting(tree, Drawn, colours, _look, Ground())
            : throw new InvalidOperationException("A drawing on screen was drawn from a tree, in colours.");

    /// <summary>
    /// The picture drawn: the one asked for, or the folder a change of folder is passing through on its
    /// way there. See <see cref="FolderFlight.Drawn"/>.
    /// </summary>
    private MapPicture Drawn => _flight.State.Drawn(_shown);

    /// <summary>The folder drawn. See <see cref="Drawn"/>.</summary>
    private int DrawnNode => Drawn.Node;

    /// <summary>
    /// Work from <paramref name="picture"/>. The marks and the hit test read every drawing in its tree
    /// from now on, so the two never name nodes of different trees.
    /// </summary>
    private void Hold(MapPicture picture)
    {
        _shown = picture;
        _marking.ReadIn(picture.Tree);
    }

    /// <summary>Work from <paramref name="drawing"/>, which is on top of the rest now.</summary>
    private void Present(ExploreSurface drawing)
    {
        var earlier = _drawing;

        _drawing = drawing;
        _arrivals.Presented();

        // A drawing that cannot be zoomed shows the whole picture whatever was asked, and the zoom
        // has to agree with it: the screen is placed, and every click resolved, by what was drawn
        // rather than by what was asked for (§7.1).
        if (drawing.Zoom is { } zoom)
        {
            _drawn = zoom.Viewport;
            _zoom.Limit(zoom.Ceiling);
        }
        else
        {
            _zoom.Reset(MapViewport.Whole);
            _drawn = MapViewport.Whole;
        }

        // At rest where it was drawn, the drawing covers the screen and is the picture: it names its
        // shapes and answers the pointer, and a tile drawn deeper would show detail it does not have.
        if (_zoom.Shown == _drawn)
        {
            _flight.Pictures.HideTiles();
        }

        Unlock();

        // Over a change of folder still flying, too: the names ride the picture they name as it moves.
        _labels.Show(drawing, _canvas.Scale, CaptionOf);

        (FrameworkElementAutomationPeer.FromElement(this) as MapAutomationPeer)?.Redrawn(_keyboard.Folder);

        // A new drawing is new geometry, so whatever was marked out is marked out somewhere else
        // now, and so is whatever the pointer is over.
        Fit();
        Place();
        Remark();
        _pointer.Reread();

        // A step out flies now that there is a picture to pull back into, from the shape the folder
        // left has in it. One that does not draw that folder's branch has nothing to fly from. A step
        // through the folder now drawn flies on into the folder asked for, from its shape in the same
        // drawing. A folder with no shape in it fades on its leg rather than flies (see MapPassage).
        if (_flight.State is { AwaitsOuter: true, Left: { } left } waiting)
        {
            var shape = drawing.ScreenOf(left.Picture.Node, _zoom.Shown, left.Viewport);

            _flight.State = waiting.OuterDrawn(shape is not null);

            if (waiting.Passing is { } through)
            {
                _flight.Descent.Pass(shape, drawing.ScreenOf(_shown.Node, _zoom.Shown, through.Opened));
            }
            else if (_flight.State.IsUnderWay)
            {
                _flight.Descent.Start(shape!.Value, FolderStep.OutOf);
            }
            else
            {
                FinishStep();
            }
        }

        // A flight in that arrived before its picture did finishes now, over it.
        if (_flight.State.Owed && !_flight.Descent.IsMoving)
        {
            FinishStep();
        }

        if (_moves.Arrive(earlier, drawing, _flight.Pictures, Placed(drawing)) == MapMove.Shift)
        {
            Follow(earlier, drawing);
        }

        // The whole picture under a zoomed drawing only shows once the picture moves, so it waits
        // until the drawing the reader asked for is on screen. Asked again as a move starts, for a
        // move that comes first.
        if (!drawing.ShownAt.IsWhole)
        {
            DispatcherQueue.TryEnqueueGuarded(DispatcherQueuePriority.Low, Underlay);
        }
    }

    /// <summary>
    /// Paint the whole picture under the drawing worked from, where it is zoomed and the whole is not
    /// there already, so a move has something to show wherever it goes. See
    /// <see cref="ExploreCanvas.Underlay"/>.
    /// </summary>
    private void Underlay()
    {
        if (_drawing is { } drawing)
        {
            _canvas.Underlay(drawing);
        }
    }

    /// <summary>
    /// The canvas ground (see <see cref="MapGround"/>), for the theme this control is in and the high
    /// contrast theme the picture on screen is drawn in.
    ///
    /// <para>The theme is read from <see cref="FrameworkElement.ActualTheme"/> rather than by pulling the
    /// brush out of the application's resource dictionary. This app applies the user's choice at element
    /// level — <c>MainWindow</c> sets <c>RequestedTheme</c> on the content root and nothing ever
    /// sets it on the application — so the application dictionary answers for the *system* theme.
    /// Asking it for a colour gives a light ground behind a dark page whenever the two disagree,
    /// which is the §6.5 failure this method exists to avoid.</para>
    /// </summary>
    private TileColour Ground() => MapGround.For(ActualTheme == ElementTheme.Dark, _contrast);

    /// <summary>
    /// What to write on a labelled shape. The blocks standing for the rest of the volume are the
    /// map's own, because they are not nodes of the page's tree.
    /// </summary>
    private MapCaption CaptionOf(ExploreLabel label) => MapBlock.Of(label.Node) is { } block
        ? MapShapeText.Caption(block.Name, label.Bytes)
        : _captions(label.Node);

    /// <summary>Mark everything the map marks out again, on the drawing it works from now. See <see cref="ExploreMarking.Remark"/>.</summary>
    private void Remark() => _marking.Remark(_drawing, _canvas.Scale);

    /// <summary>
    /// Outline what the pointer is over, and the shape the keyboard is on, in the drawing the map works
    /// from. See <see cref="ExploreMarking.ShowHovered"/>.
    /// </summary>
    private void ShowHovered() =>
        _marking.ShowHovered(_drawing, _pointer.Over?.Hit, _pointer.Over?.Tile, KeyboardShape, _drawing is { } drawing ? _flight.Pictures.SurfaceOf(drawing) : null);

    /// <summary>
    /// The shape the keyboard is on, while the keyboard has the map, in a drawing of the tree the page
    /// handed over last, which is the one the keyboard's nodes are numbers in; and null otherwise.
    /// </summary>
    private MapShape? KeyboardShape => _keyboard.ShapeIn(Readable);

    /// <summary>
    /// Where <paramref name="point"/>, in the control's own coordinates, falls in the drawing that
    /// shows there, or null where none does.
    ///
    /// <para>Mapped through the drawing's own dimensions rather than through the display scale,
    /// because the two part company for as long as a resize is still settling. The canvas is
    /// stretched over the control's new bounds in the meantime, so what is on screen is the old
    /// canvas scaled to fit, and a pointer mapped by the display scale would answer from geometry
    /// that is no longer where it is drawn.</para>
    ///
    /// <para>§7.1 makes that a safety question rather than a cosmetic one. A right-click picks
    /// what the menu then acts on, so a pick that disagrees with the picture is a Delete aimed at
    /// something the user never pointed at — the same mistake <c>ExploreInput</c> avoids by picking
    /// from the row under the pointer rather than from the last selection.</para>
    ///
    /// <para>A zoom or a drag on its way is the same case again. The screen shows the drawing moved
    /// and magnified, so the point is taken back through that placement first. A point the moved
    /// drawing does not reach is over nothing, even where a shape running off the drawing's edge would
    /// contain it, and even though a kept drawing shows there: that one is at another zoom, it names
    /// fewer shapes than the drawing that replaces it a moment later, and the outline that would
    /// confirm the pick is drawn in this drawing's geometry, not in its. A change of folder is over
    /// nothing while it flies, and while a step out waits for the picture it flies out to, for the
    /// same reason: the picture is on its way to being another one. A press lands it first.</para>
    ///
    /// <para>A redraw landing is the case once more. Its regions show where they have landed and the
    /// last drawing shows everywhere else, so each point is answered by whichever shows there: see
    /// <see cref="MapHitTest"/>, which also reads a drawing of an older tree of the same place in the
    /// newer one, and refuses an answer from a drawing of anything else. So are the tiles a move shows
    /// over the drawing, which draw the folders it has opened.</para>
    /// </summary>
    private MapLocation? Locate(Point point) =>
        !_canvas.HasSize || Stepping || _overview.Covers(point)
            ? null
            : MapHitTest.Locate(
                _zoom.Shown, point.X / ActualWidth, point.Y / ActualHeight, _drawing,
                _moves.IsOwed ? null : _canvas.Arriving,
                _readingOf,
                _tileOver);

    /// <summary>
    /// Whether the screen is between two pictures: a change of folder flying or waiting for the
    /// picture it is to fly out to, shapes moving to a new layout, or a new drawing fading in.
    /// </summary>
    private bool Stepping => _flight.Descent.IsMoving || _flight.State.AwaitsOuter || _moves.IsMoving || _flight.Pictures.IsFading;

    /// <summary>
    /// Zoom at the pointer, or with Shift held, or on a wheel that tilts, pan across, where the page
    /// allows it and the picture can be zoomed. Ctrl and the wheel never arrive here: the compositor
    /// zooms for them itself.
    ///
    /// <para>Left unhandled otherwise, so a wheel over a map that does not zoom goes on to whatever
    /// would have had it.</para>
    /// </summary>
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_zoom.Movable || !_canvas.HasSize)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var delta = point.Properties.MouseWheelDelta;

        PrepareMove();

        _pointer.MoveTo(point.Position);
        _overview.Avoid(point.Position);

        // A wheel tilted right reports a turn away from the reader, and shows what is to the right.
        if (point.Properties.IsHorizontalMouseWheel)
        {
            _zoom.Slide(-delta);
        }
        else if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            _zoom.Slide(delta);
        }
        else
        {
            _zoom.Turn(delta, point.Position.X / ActualWidth, point.Position.Y / ActualHeight);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Move between the shapes, or move the picture, as <see cref="MapKeys.Resolve"/> says the key
    /// does. The picture moves only where it can.
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (Resolve(e))
        {
            case MapKeyAction.Focus focus:
                SettleStep();

                e.Handled = _keyboard.Arrow(Readable, focus.Direction);
                break;

            case MapKeyAction.Camera camera when _zoom.Movable:
                PrepareMove();

                _zoom.Press(camera.Key);

                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Enter opens the shape the keyboard is on, and Backspace goes up a folder, on the map itself. Taken
    /// before the page's own keys for them, which open what is picked and go back.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (Resolve(e))
        {
            case MapKeyAction.Open when Reader is { } reader && reader.FocusedShape() is { } focused && reader.Drawn(focused) is { } shape:
                reader.Open(focused.Folder, shape);
                e.Handled = true;
                break;

            case MapKeyAction.Ascend when Ascending is { } ascending:
                ascending(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }

    /// <summary>What the key in <paramref name="e"/> does, with the modifiers held now and whether it reached the map itself.</summary>
    private MapKeyAction? Resolve(KeyRoutedEventArgs e) =>
        MapKeys.Resolve(e.Key, Held(VirtualKey.Shift), Held(VirtualKey.Menu), Held(VirtualKey.Control), ReferenceEquals(e.OriginalSource, this));

    private static bool Held(VirtualKey modifier) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(modifier).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// One report of the camera on its way: cut the picture at the edges if it now runs past them, and
    /// say what is under the pointer now that the picture has moved beneath it.
    ///
    /// <para>The picture and the names follow the camera on the compositor, and the names fade as their
    /// shapes shrink round them, so nothing here moves them.</para>
    /// </summary>
    private void OnZoomMoved(object? sender, EventArgs e)
    {
        Place();
        ShowOverview();
        Sharpen();

        // Not Reread, which marks the shape out again whether or not it changed. That is right once
        // per new drawing and wrong at every frame of a move over the same one: the outline is in the
        // drawing's own pixels and moves with the placement, and redrawing it is a pass over every
        // shape and two new geometries, sixty times a second.
        _pointer.Follow();
    }

    /// <summary>
    /// Show the tiles of the picture the screen shows as the camera moves, and draw those it is missing
    /// (see <see cref="ExploreCanvas.Sharpen"/>). Not while a change of folder flies or new shapes
    /// travel, which move a picture on its way to being another one.
    /// </summary>
    private void Sharpen()
    {
        if (Stepping)
        {
            return;
        }

        // Where the drawing on top was made, it is the picture pixel for pixel, and a tile, drawn finer
        // and shown smaller over it, only blurs every line in it. The camera reports a jump there with
        // no move to come to rest from, as its bounds are first set while the page is still arriving,
        // so nothing would take the tiles off again.
        if (ShowsTheDrawingAsDrawn)
        {
            _flight.Pictures.HideTiles();
            return;
        }

        _canvas.Sharpen(Ground());
    }

    /// <summary>
    /// Whether the screen shows the picture exactly where the drawing on top was made, with no redraw
    /// still landing over it, so that drawing is all there is to show.
    /// </summary>
    private bool ShowsTheDrawingAsDrawn => _zoom.Shown == _drawn && _canvas.Arriving is not { IsSettled: false };

    /// <summary>
    /// Settle any change of folder still flying, whichever button went down, so what the press resolves
    /// against is the picture on screen rather than one on its way; and get ready to drag the picture
    /// with the left or middle button, where the page allows it and the picture is zoomed.
    ///
    /// <para>Nothing moves yet, and the press is left for the framework to make a click of. Whether
    /// it is a drag is decided by how far it goes: see <see cref="MapDrag"/>.</para>
    /// </summary>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        _press.Press(point.PointerDeviceType == PointerDeviceType.Mouse, point.Properties.IsLeftButtonPressed, _moves.IsMorphing);

        // The pointer has the map now: the shape the keyboard was on is not marked until a key moves again.
        if (_keyboard.Leave())
        {
            ShowHovered();
        }

        SettleStep();

        if (!_zoom.Movable)
        {
            _drag.Press(point.Position.X, point.Position.Y, movable: false);
            return;
        }

        // A move on its way stops where it is, so the press lands on what the screen shows.
        _zoom.Halt();

        if (point.PointerDeviceType == PointerDeviceType.Touch)
        {
            _drag.Press(point.Position.X, point.Position.Y, movable: false);
            _zoom.Redirect(point);
            return;
        }

        var movable = !_zoom.Shown.IsWhole && Drags(point);

        _drag.Press(point.Position.X, point.Position.Y, movable);

        if (movable)
        {
            // A drag moves the picture out from under whatever it has not drawn yet.
            Underlay();
        }
    }

    /// <summary>Whether <paramref name="point"/> has a button down that drags the picture: the left, or the middle.</summary>
    private static bool Drags(PointerPoint point) =>
        point.Properties.IsLeftButtonPressed || point.Properties.IsMiddleButtonPressed;

    /// <summary>
    /// Drag the picture with the left or middle button held, once it has moved past what a click allows, and
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

        _pointer.MoveTo(position);

        if (_canvas.HasSize
            && _drag.Move(position.X, position.Y, Drags(point)) is (var x, var y))
        {
            if (!began)
            {
                CapturePointer(e.Pointer);
                ProtectedCursor = _dragCursor;
            }

            _overview.Avoid(position);
            _zoom.Drag(x, y, position.X, position.Y, TimeSpan.FromMicroseconds(point.Timestamp));

            e.Handled = true;
            return;
        }

        _pointer.Follow();
    }

    /// <summary>
    /// The button came up: focus the map, so the keyboard moves the picture next, as it does a map
    /// anywhere else, and end any drag. Focused here rather than as the button goes down, because the
    /// page's scroller takes the focus for itself on every press that reaches it. Through the pointer,
    /// so no focus rectangle is drawn round a map the reader has only clicked.
    /// </summary>
    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_zoom.Movable)
        {
            Focus(FocusState.Pointer);
            e.Handled = true;
        }

        OnDragEnded(sender, e);
    }

    /// <summary>The button came up, or the pointer was taken away: the drag is over and worth drawing.</summary>
    private void OnDragEnded(object sender, PointerRoutedEventArgs e)
    {
        if (EndDrag())
        {
            _zoom.Release(TimeSpan.FromMicroseconds(e.GetCurrentPoint(this).Timestamp));
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

    /// <summary>Say what the pointer found.</summary>
    private void Report(ExploreHit? hit) => Hovered?.Invoke(this, hit);

    /// <summary>
    /// Put the pointer's light where <paramref name="spot"/> is, or take it away: see
    /// <see cref="PointerLight"/>.
    ///
    /// <para>Only on the drawing at rest on screen, where it was shaded on the GPU, and only while effects
    /// are fast. Not on a tile, which a move shows for a moment, nor on a drawing on its way in, and not in
    /// high contrast, where the picture is the theme's and nothing is added to it (§6.5). Not where the
    /// reader turned it off, and as bright as they chose where they did not (<see cref="MapPointerLight"/>).</para>
    /// </summary>
    private void Shine(MapLocation? spot)
    {
        if (spot is { Tile: null } found
            && _look.Light.Shown
            && ReferenceEquals(found.Drawing, _drawing)
            && _graphics.Shades
            && !Contrasted)
        {
            _flight.Pictures.Light(found.Drawing, PointerLight.At(found.X, found.Y, _canvas.Scale, _look.Light.Amount));
        }
        else
        {
            _flight.Pictures.Light(null, null);
        }
    }

    /// <summary>
    /// Redraw only when the scale actually moved. The root raises this for several reasons — the
    /// window changing host among them — and rasterising a full volume for each would be a repaint
    /// for something that did not change a pixel.
    /// </summary>
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // A window minimised or hidden shows no move to sharpen: its tiles, and what the device keeps
        // for itself, go, and the next move draws them again.
        if (!sender.IsHostVisible)
        {
            _flight.Pictures.ReleaseTiles();
            _flight.Departing.ReleaseTiles();
            _graphics.Device.Trim();
        }

        if (Math.Abs(sender.RasterizationScale - _canvas.Scale) > 0.001)
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
        _generation = _graphics.Device.Generation;

        LandStep();
        _canvas.Cancel();
        _flight.Departing.Renew();
        _flight.Pictures.Renew();

        Forget();
        _labels.Hide();
        _highlight.Renew();
        Report(null);

        Redraw();
    }

    /// <summary>
    /// Follow a change of the system's look. A change of accent colour or of whether effects are fast
    /// restyles the outlines. High contrast turned on or off or to another theme draws the picture
    /// again in the colours it now has, and so does a new text size, because the bands and the label
    /// thresholds are sized for the old one.
    /// </summary>
    private void OnLookChanged(object? sender, SystemLookChange change)
    {
        if ((change & (SystemLookChange.Colours | SystemLookChange.Effects)) != 0)
        {
            TintHighlight();
        }

        if ((change & SystemLookChange.TextScale) != 0
            || ((change & SystemLookChange.Colours) != 0 && SystemLook.Current.State.Contrast?.ToMap() != _contrast))
        {
            Redraw();
        }
    }

    /// <summary>
    /// Follow the reader turning animation effects on or off, which says whether the picture coasts
    /// and stretches. Raised on the UI thread.
    /// </summary>
    private void OnMotionChanged(object? sender, EventArgs e) => _zoom.FollowMotion();

    /// <summary>
    /// The lightest of the accent's shades for what the pointer is over, because it is drawn over a
    /// dark halo in either theme; or in high contrast the theme's own colours for selected and hot
    /// items, over its window colour.
    /// </summary>
    private void TintHighlight()
    {
        // One reading for both, so the overview and the outlines cannot disagree about the theme.
        var look = SystemLook.Current.State;

        // The overview's mark in white over a dark halo, which reads over any colour in the picture,
        // or in high contrast the theme's own colour for what is selected, over its window colour.
        if (look.Contrast is { } contrast)
        {
            _overview.Restyle(contrast.Highlight, contrast.Window);
        }
        else
        {
            _overview.Restyle(Microsoft.UI.Colors.White, Windows.UI.Color.FromArgb(170, 0, 0, 0));
        }

        _highlight.Restyle(look.Accent, look.Contrast?.ToHighlight(), look.EffectsFast);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_press.TapPicks(_drag.Dragged))
        {
            Pick(e.GetPosition(this));
        }
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // A picture that answers nothing has nothing to offer a menu for (§7.1).
        if (!Answers || !_press.RightTapActs)
        {
            return;
        }

        var point = e.GetPosition(this);

        Pick(point);
        MenuRequested?.Invoke(this, point);
    }

    /// <summary>Pick what is at <paramref name="point"/>, as <see cref="MapPick.Click"/> says a click there does.</summary>
    private void Pick(Point point)
    {
        var pictured = _drawing is not null || _canvas.Arriving is { Landed.Count: > 0 };

        if (MapPick.Click(Answers, pictured, Locate(point)?.Hit) is { } pick)
        {
            Picked?.Invoke(this, pick.Node);
        }
    }

    /// <summary>
    /// Open what was double-clicked, and zoom to it where it cannot be opened.
    ///
    /// <para>The page is what decides whether a node opens, and it says so by showing it here before
    /// <see cref="Activated"/> returns. The camera then flies into the folder's shape, as it does for
    /// every step into a folder however the page asked for it (see <see cref="Show(ISizedTree, int, ExploreView, Func{DateTime, ShapeColours}, Func{int, MapCaption}, MapLook, VolumeSpace, MapViewport?)"/>).
    /// What does not open is zoomed to: see <see cref="MapOpen"/>.</para>
    /// </summary>
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(this);

        if (!_press.DoubleTapOpens(_drag.Dragged) || Locate(point) is not { } spot || spot.Hit is not { } hit)
        {
            return;
        }

        // Only a treemap nests: an icicle draws what a shape holds below it rather than in it.
        Open(spot.Drawing, hit, spot.Drawing.Zoom is null ? null : spot.ShapeOnScreen(_zoom.Shown));
    }

    /// <summary>
    /// Open <paramref name="hit"/>, drawn in <paramref name="drawing"/> where <paramref name="shape"/>
    /// is on the screen, and zoom to it where it cannot be opened: what a double-click does, and Enter on
    /// the shape the keyboard is on. See <see cref="OnDoubleTapped"/>.
    /// </summary>
    private void Open(ExploreSurface drawing, ExploreHit hit, MapFrame? shape)
    {
        if (MapOpen.AsksPage(hit, _shown.Node, drawing.HasVolumeBeside))
        {
            // Counted rather than compared by node: the root opened out of its volume is the same
            // node drawn as another picture.
            var handedBefore = _picturesHanded;

            Activated?.Invoke(this, hit.Node);

            if (_picturesHanded != handedBefore)
            {
                return;
            }
        }

        if (MapOpen.ZoomTo(Zoomable, shape, _zoom.Shown, _zoom.Ceiling) is { } fitting)
        {
            // Already going there, which is a step to nowhere.
            if (fitting != _zoom.Target)
            {
                ZoomingIn?.Invoke(this, _zoom.Target);
            }

            Underlay();
            _zoom.GlideTo(fitting);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new MapAutomationPeer(this);

    /// <summary>
    /// The reader of the drawing on screen (<see cref="Readable"/>), one for each drawing, or null while
    /// there is none. What a screen reader and the keyboard read of the map's shapes, and how they act on
    /// them.
    /// </summary>
    internal MapReader? Reader => Readable is { } drawing
        ? _reader is { } kept && kept.Reads(drawing) ? kept : _reader = new MapReader(this, drawing, _keyboard, _marking.Picked, _zoom)
        : null;

    /// <summary>
    /// The drawing the keyboard and a screen reader work from: the one the map works from, while it is
    /// of the tree the page handed over last, which the page's facts, the selection and the keyboard's
    /// shape are numbered in. Null while there is none, or one of an older tree is still on screen.
    /// </summary>
    private ExploreSurface? Readable => _drawing is { } drawing && ReferenceEquals(drawing.Tree, _shown.Tree) ? drawing : null;

    /// <summary>Whether what is picked on this picture is acted on: not on a step of a replay (<see cref="MapReading.Answers"/>).</summary>
    private bool Answers => MapReading.Answers(_shown.Tree);

    /// <summary>Pick nothing, as a click on empty space does.</summary>
    internal void Unpick() => Picked?.Invoke(this, null);

    /// <summary>
    /// Open <paramref name="shape"/> of <paramref name="drawing"/>, or zoom to it where it cannot be
    /// opened: what Enter and a double-click do. Nothing where that drawing is no longer on screen once
    /// a change of folder still flying has landed, whose node numbers may be other nodes (§7.1).
    /// </summary>
    internal void OpenShape(ExploreSurface drawing, int? folder, MapShape shape)
    {
        SettleStep();

        if (!ReferenceEquals(drawing, Readable))
        {
            return;
        }

        _keyboard.Remember(drawing, folder, shape);

        Open(
            drawing,
            new ExploreHit(shape.Node, shape.Bytes),
            drawing.Zoom is null ? null : drawing.PlacementOn(_zoom.Shown).OnScreen(shape.Frame));
    }

    /// <summary>
    /// The keyboard landed on a shape: it is marked, the line under the map names it, the camera brings
    /// it into view, and a screen reader hears it. Picked as well where the landing says, a block picking
    /// nothing as a click on one does.
    /// </summary>
    private void OnKeyboardLanded(object? sender, ExploreKeyboardLanding landing)
    {
        var (drawing, folder, shape, pick) = landing;
        var hit = new ExploreHit(shape.Node, shape.Bytes);

        if (pick && MapPick.Landing(Answers, hit) is { } picked)
        {
            Picked?.Invoke(this, picked.Node);
        }

        ShowHovered();
        Report(hit);
        Bring(drawing, shape);

        if (FrameworkElementAutomationPeer.FromElement(this) is not MapAutomationPeer peer)
        {
            return;
        }

        // Into another folder: the shapes a screen reader is given follow the keyboard's path.
        peer.KeyboardMoved(_keyboard.Folder);

        if (AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged) && peer.PeerOf(folder, shape.Node) is { } focused)
        {
            focused.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
        }
    }

    /// <summary>
    /// Move the camera as little as brings <paramref name="shape"/> into view at a size it can be seen at
    /// (<see cref="MapViewport.Showing"/>): big enough to carry its name, at the reader's text size.
    /// </summary>
    internal void Bring(ExploreSurface drawing, MapShape shape)
    {
        if (!_zoom.Movable || drawing.Zoom?.Viewport is not { } drawn || !_canvas.HasSize)
        {
            return;
        }

        var text = SystemLook.Current.State.TextScale;
        var target = _zoom.Target.Showing(
            drawn.PictureOf(shape.Frame),
            LayoutLimits.Default.MinimumLabelWidth * text / ActualWidth,
            LayoutLimits.Default.MinimumLabelHeight * text / ActualHeight,
            _zoom.Ceiling);

        if (target != _zoom.Target)
        {
            Underlay();
            _zoom.GlideTo(target);
        }
    }

    /// <summary>
    /// The map has the focus from the keyboard or a screen reader: the keyboard is on the shape it was
    /// on, or the one picked, or the first, and it is marked and named.
    /// </summary>
    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, this) || FocusState is not (FocusState.Keyboard or FocusState.Programmatic))
        {
            return;
        }

        _keyboard.TakeFocus(Readable);
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, this))
        {
            _keyboard.Leave();
            ShowHovered();
        }
    }

    /// <summary>
    /// The menu key or Shift+F10 on the map: the menu opens on the shape the keyboard is on, picked first
    /// as a right-click picks, at the middle of where the camera is taking it. Where the keyboard has not
    /// been moving, on what a click picked and nothing else, because that is what the reader chose
    /// (§7.1); and not at all where a click picked nothing. A right-click has a position and is
    /// <see cref="OnRightTapped"/>'s.
    /// </summary>
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.TryGetPosition(this, out _) || !Answers || Readable is not { } drawing)
        {
            return;
        }

        if (_keyboard.Menu(drawing) is not { } shape)
        {
            return;
        }

        var (x, y) = drawing.PlacementOn(_zoom.Target).OnScreen(shape.X, shape.Y);

        MenuRequested?.Invoke(this, new Point(Math.Clamp(x, 0, 1) * ActualWidth, Math.Clamp(y, 0, 1) * ActualHeight));

        e.Handled = true;
    }

    /// <summary>
    /// The keyboard goes to the picture <paramref name="change"/> hands over, as
    /// <see cref="ExploreKeyboard.Carry"/> says, and the folders a screen reader expanded go with it
    /// (<see cref="MapAutomationPeer.Carry"/>), both through the counterparts the change read once.
    /// </summary>
    private void CarryFocus(PictureChange change)
    {
        _keyboard.Carry(change);

        (FrameworkElementAutomationPeer.FromElement(this) as MapAutomationPeer)?.Carry(change.Counterparts);
    }

    /// <summary>
    /// Hand the set of layers on screen to the picture being left, held where the camera had it at
    /// <paramref name="left"/>, and draw the new picture into the other set, which follows the camera
    /// and takes the names and the overview. See <see cref="ExploreFlight.Swap"/>.
    /// </summary>
    private void Swap(MapViewport left)
    {
        _flight.Swap(left.Camera(ActualWidth, ActualHeight, _zoom.Origin), _zoom.Origin);
        _labels.Ride(_flight.Pictures.Root);
        _overview.ShowWhole(_flight.Pictures.Overview);
    }

    /// <summary>
    /// The reader asked for the folder the flight is leaving before it arrived, at the zoom they left it
    /// at: the flight turns round from where it is, at the speed it has, and the drawing left, still on
    /// screen, is the one the map works from again. Nothing is drawn afresh, and nothing jumps.
    /// </summary>
    private void Return(ExploreSurface drawing, Func<DateTime, ShapeColours> colours, Func<int, MapCaption> captions, MapLook look)
    {
        var left = _flight.State.Left!;
        var leaving = _zoom.Shown;

        _canvas.Cancel();

        // The folder being left is the one drawn: on the way out of a step through a folder, the
        // folder passed through, which the drawing kept is of.
        _flight.State = _flight.State.Turned(new FolderLeft(Drawn, leaving, _drawing, _shapeColours));

        _zoom.Reset(left.Viewport);
        Swap(leaving);

        Hold(left.Picture);
        _colours = colours;
        _captions = captions;
        _look = look;
        _shapeColours = left.Colours;

        // A step out still waiting for its picture had not moved yet: there is nothing to turn round.
        var flying = _flight.Descent.IsMoving;

        _flight.Descent.Turn();
        Present(drawing);

        if (!flying)
        {
            FinishStep();
        }
    }

    /// <summary>
    /// Get ready for the camera to move: settle any change of folder still flying, so the move starts
    /// from the picture on screen, and paint the whole picture under a zoomed one, so the move has
    /// something to show wherever it goes. Every input that moves the camera asks this first.
    /// </summary>
    private void PrepareMove()
    {
        SettleStep();
        Underlay();
    }

    /// <summary>
    /// Bring a change of folder on its way to where it was going, for an input that needs the screen
    /// settled first: the flight lands, and a step out still waiting for its picture stops waiting,
    /// so the picture left goes. A flight that landed before its picture arrived still waits for it.
    /// A step through a folder still waiting for the picture of that folder goes straight to the
    /// folder asked for, which is drawn in its place.
    /// </summary>
    private void SettleStep()
    {
        _moves.Finish();
        _flight.Pictures.LandFades();

        if (_flight.Descent.IsMoving)
        {
            _flight.Descent.Finish();
        }
        else if (_flight.State.AwaitsOuter)
        {
            if (_flight.State.Passing is { } through)
            {
                _flight.State = FolderFlight.None;
                _zoom.Reset(through.Opened);
                Redraw();
            }

            FinishStep();
        }
    }

    /// <summary>
    /// End a change of folder on its way altogether, for a map handed another picture or one with
    /// nothing to draw: the picture left goes, whether or not the one it was going to has arrived.
    /// </summary>
    private void LandStep()
    {
        SettleStep();

        if (_flight.State.IsUnderWay)
        {
            FinishStep();
        }
    }

    /// <summary>
    /// Keep the reader on what they zoomed into, now that <paramref name="later"/> has laid the same
    /// place out again where <paramref name="earlier"/> was on screen (<see cref="MapFollow"/>): the
    /// camera moves with the shape at the middle of the screen over the same time as the shapes travel,
    /// so that shape holds still while the rest moves round it. Only from a camera at rest where both
    /// were drawn: one the reader is moving is the reader's.
    /// </summary>
    private void Follow(ExploreSurface? earlier, ExploreSurface later)
    {
        if (MapFollow.From(earlier, later, _zoom.Shown, _zoom.Target, _zoom.Movable) is not { } onward)
        {
            return;
        }

        var motion = SystemMotion.Current.For(MotionToken.Shift);

        // With animation effects off nothing travels, and the camera goes straight to where the shape is.
        _zoom.MoveTo(onward, motion.Travels ? motion : Motion.Instant);
    }

    /// <summary>A new drawing has finished fading in: the pointer is over what it shows.</summary>
    private void OnFaded(object? sender, EventArgs e) => _pointer.Reread();

    /// <summary>
    /// A tile has come in under a pointer that has not moved, or one it was over has gone: the pointer
    /// is over what the screen shows now (§7.1).
    /// </summary>
    private void OnTileAnswersChanged(object? sender, EventArgs e) => _pointer.Reread();

    /// <summary>
    /// Shapes have started to travel, to a new layout or into a new view: the names and the outlines,
    /// which do not move with them, go, and the pointer is over nothing until they land (§7.1).
    /// </summary>
    private void OnMoveStarted(object? sender, EventArgs e)
    {
        _labels.Hide();
        _highlight.Hide();
        _pointer.Reread();
    }

    /// <summary>
    /// The shapes have landed, from a new layout or into a new view: the names, the outlines and the
    /// readout are the new drawing's again.
    /// </summary>
    private void OnMoveLanded(object? sender, EventArgs e)
    {
        _labels.Reveal();
        _highlight.Reveal();
        _pointer.Reread();
    }

    /// <summary>
    /// A flight through a folder has reached the picture of the folder that holds both ends, which
    /// fills the screen: from here it is a step into the folder asked for, out of that picture. The
    /// folder left goes, the picture passed through is held where it is to fly out of, and the folder
    /// asked for is drawn into the other set of layers, arriving into the flight as a step in's does.
    /// </summary>
    private void OnDescentMidway(object? sender, EventArgs e)
    {
        var through = _flight.State.Passing!;
        var passing = _zoom.Shown;

        _flight.State = _flight.State.Midway(_shown, passing, _drawing, _shapeColours);

        // Emptied of the folder left, because the two sets change places below and the folder asked
        // for is drawn into this one.
        _flight.Departing.Clear();
        _zoom.Reset(through.Opened);
        Swap(passing);

        _drawing = null;
        _labels.Hide();
        _highlight.Hide();

        Redraw();
    }

    /// <summary>
    /// The flight has arrived: the picture left goes, and the names, the outlines and the readout are
    /// the new drawing's from here. Unless that drawing is still landing, in which case the picture
    /// left stays under it, where the flight left it, until it arrives.
    /// </summary>
    private void OnDescentArrived(object? sender, EventArgs e)
    {
        _flight.State = _flight.State.Arrived(landing: _drawing is null && _canvas.Pending is not null);

        if (!_flight.State.Owed)
        {
            FinishStep();
        }
    }

    /// <summary>The end of a change of folder, once its flight is over and its picture has arrived.</summary>
    private void FinishStep()
    {
        _flight.Land();

        // Marked out again for the drawing worked from now, before they show: one settled before its
        // picture arrived has none, and the hatch and outlines hidden with the picture left belong to
        // its shapes, which in this picture are other shapes or none (§7.1).
        Remark();

        _labels.Reveal();
        _highlight.Reveal();
        Place();
        ShowOverview();
        _pointer.Reread();
    }
}
