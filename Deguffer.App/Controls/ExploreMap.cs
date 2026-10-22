using System.Numerics;
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
/// the dimming round a selection, the shape under the pointer lifted and the hatch over what is
/// being removed, so a click moves a line rather than rasterising a volume again.</para>
///
/// <para>Nothing here knows what a drive is, how one is scanned, or how any of the views are laid
/// out. It is handed a tree, a node and a view, it asks Core for the matching
/// <see cref="ExploreSurface"/>, and it reports back what was pointed at (G1).</para>
///
/// <para>Over 500 lines because it is where the pointer, the drawings and the page meet, and each
/// input has to be resolved against whichever picture is on screen at that moment (§7.1). Everything
/// that can stand apart does: the drawings kept (<see cref="ExploreLayers"/>), the camera and what
/// moves it (<see cref="ExploreZoom"/>), the clock of a change of folder (<see cref="ExploreDescent"/>), the names and
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
    /// Which way the change of folder on its way goes, from the moment the page hands the map another
    /// folder until the flight has finished and the picture it went to has arrived; and
    /// <see cref="FolderStep.Across"/> while there is none. The pointer is over nothing while it flies,
    /// because the screen is on its way from one picture to another (§7.1): see <see cref="Stepping"/>.
    /// </summary>
    private FolderStep _step;

    /// <summary>
    /// The folder the change of folder on its way left, and the drawing of it, so a reader who asks
    /// for it back before the flight has arrived is flown back from where the flight is.
    /// </summary>
    private Left? _left;

    /// <summary>
    /// Whether a step out is waiting for the picture it goes out to: until that is drawn, there is
    /// nothing to pull back into, and the picture left stays filling the screen.
    /// </summary>
    private bool _awaitingOuter;

    /// <summary>
    /// The folder that holds both ends of a change of folder between folders where neither holds the
    /// other, while the flight is on its way out to it: the picture drawn in the meantime is of this
    /// folder, which the flight passes through, rather than of the folder asked for. Null otherwise,
    /// and from the middle of that flight on. See <see cref="FolderStep.Through"/>.
    /// </summary>
    private Through? _through;

    /// <summary>
    /// Whether a flight into a folder has finished and the picture of it has not arrived yet. The old
    /// picture stays, stretched where the flight left it, until the new one has landed over it.
    /// </summary>
    private bool _stepOwed;

    /// <summary>
    /// Whether the last press was one a tap may act on: the left button, a touch or a pen. The
    /// framework makes a tap and a double-tap of the mouse's Back and Forward buttons too, which pick
    /// and open nothing anywhere else in Windows. Where the page goes back or forward with them,
    /// picking or opening the shape under the pointer as well would act on a picture the step is
    /// about to replace.
    /// </summary>
    private bool _tapPress = true;

    /// <summary>
    /// Whether the last press landed a change of view on its way. It takes no click: the shapes were
    /// where neither view puts them, so whatever the press was aimed at is not what is under it now (§7.1).
    /// </summary>
    private bool _pressLandedMorph;

    /// <summary>
    /// Whether the press before the last one landed a change of view: a double-click is two presses,
    /// and the second finds the move already landed by the first.
    /// </summary>
    private bool _earlierPressLandedMorph;

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
    /// <see cref="MapGraphics.Generation"/>.
    /// </summary>
    private int _generation;

    private readonly DispatcherQueueTimer _settled;

    private readonly ExploreZoom _zoom;

    private readonly ExploreDescent _descent = new(SystemMotion.Current, RenderingClock.Current);

    /// <summary>The large shapes moving from one layout of the same place to the next. See <see cref="ExploreShift"/>.</summary>
    private readonly ExploreShift _shift;

    /// <summary>
    /// Whether the redraw on its way is the same place laid out again, which moves from the drawing
    /// on screen when it arrives rather than replacing it: a scan's next snapshot, or a new spacing.
    /// </summary>
    private bool _shiftOwed;

    /// <summary>The large shapes travelling from one view to another. See <see cref="ExploreMorph"/>.</summary>
    private readonly ExploreMorph _morph;

    /// <summary>
    /// Whether the redraw on its way is the same place in another view, which the largest shapes travel
    /// into from the drawing on screen when it arrives.
    /// </summary>
    private bool _morphOwed;

    /// <summary>The pointer's look while it drags the picture. One for the life of the map (G5).</summary>
    private readonly InputCursor _dragCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    /// <summary>
    /// The drawings of the picture on screen. See <see cref="ExploreLayers"/>. Swapped with
    /// <see cref="_departing"/> as the folder changes, so the old picture can go on showing while the
    /// camera flies from it to the new one.
    /// </summary>
    private ExploreLayers _pictures;

    /// <summary>The picture a change of folder is leaving, while it does, and empty otherwise.</summary>
    private ExploreLayers _departing;

    /// <summary>
    /// How many different pictures <see cref="Show(ISizedTree, int, ExploreView, Func{DateTime, ShapeColours}, Func{int, string}, MapLook, VolumeSpace)"/>
    /// has been handed, so a double-click can tell whether the page opened what it asked for.
    /// </summary>
    private int _picturesHanded;

    /// <summary>Whether the left or middle button is dragging the picture. See <see cref="MapDrag"/>.</summary>
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

    /// <summary>What a removal under way is acting on, and the tree it is in. See <see cref="MarkRemoving"/>.</summary>
    private readonly HashSet<int> _marked = [];

    private ISizedTree? _markedTree;

    /// <summary>Whether the compositor can draw effects without slowing the picture down, and when that changes.</summary>
    private readonly CompositionCapabilities _capabilities = new();

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

    /// <summary>How much room a treemap leaves round what each folder holds, and how every shape is finished.</summary>
    private MapLook _look = MapLook.Default;

    /// <summary>
    /// The volume to draw beside the node, or <see cref="VolumeSpace.None"/>; the page decides. See
    /// <see cref="ExploreSurface.Create(ISizedTree, int, ExploreView, int, int, double, double, ShapeColours, ExploreSpacing, VolumeSpace, MapViewport, MapStyle)"/>
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

    /// <summary>
    /// The high contrast theme's colours the drawings of the picture on screen are in, or null where
    /// they are coloured and shaded: read with <see cref="_shapeColours"/>, so the ground under them
    /// agrees with them, and read again to tell whether a change of the system's colours changed them.
    /// </summary>
    private ContrastColours? _contrast;

    private double _scale = 1;
    private ExploreHit? _hovered;

    /// <summary>
    /// The tile <see cref="_hovered"/> was found on, while the camera moves, or null where it was found
    /// on a drawing. Its outline is the tile's, because the drawing worked from may not draw it.
    /// </summary>
    private MapTile? _hoveredTile;

    /// <summary>
    /// The finest tile shown over the drawing worked from at a point of the picture, asked of whichever
    /// set of layers is the picture on screen at the time. One for the life of the map, because the
    /// pointer asks at the display's rate (G5).
    /// </summary>
    private readonly Func<double, double, MapTile?> _tileOver;

    /// <summary>
    /// How a drawing is read in the tree the page handed over last, for <see cref="MapHitTest"/>. One
    /// for the life of the map, because the pointer asks at the display's rate (G5). See
    /// <see cref="ReadingOf"/>.
    /// </summary>
    private readonly Func<ISizedTree, MapReading?> _readingOf;

    /// <summary>The last reading found, kept while it is of the same two trees. See <see cref="ReadingOf"/>.</summary>
    private MapReading? _reading;

    /// <summary>What a card beside the map lights on it, or null. See <see cref="Light"/>.</summary>
    private MapLight? _light;

    /// <summary>Which shape the keyboard is on. See <see cref="ExploreFocus"/>.</summary>
    private readonly ExploreFocus _focus = new();

    /// <summary>
    /// The folders a screen reader expanded on this picture, which list their shapes as the keyboard's
    /// path does (<see cref="MapFolders.Lists"/>). Carried with the keyboard to a new tree of the same place.
    /// </summary>
    private readonly HashSet<int> _expanded = [];

    /// <summary>
    /// Whether the map has the focus from the keyboard, or from a screen reader, rather than from a click:
    /// what marks the shape the keyboard is on and makes Enter open it. A click focuses the map too, so
    /// the keys go to it, and marks nothing until a key moves between the shapes.
    /// </summary>
    private bool _keyboard;

    public ExploreMap()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

        _graphics = MapGraphics.For(compositor);
        _generation = _graphics.Generation;
        _camera = new MapCamera(compositor);
        _zoom = new ExploreZoom(ElementCompositionPreview.GetElementVisual(this), _camera, SystemMotion.Current, DispatcherQueue);
        var owner = new DispatcherQueueSynchronizationContext(DispatcherQueue);

        _pictures = new ExploreLayers(compositor, _graphics, _camera, new ExploreTiles(compositor, _graphics, SystemMotion.Current, owner));
        _departing = new ExploreLayers(compositor, _graphics, _camera, new ExploreTiles(compositor, _graphics, SystemMotion.Current, owner));
        _tileOver = (x, y) => _pictures.Tiles.TileOver(x, y);
        _readingOf = ReadingOf;
        _highlight = new ExploreHighlight(compositor, _graphics.Geometry, _camera);
        _shift = new ExploreShift(compositor, _camera);
        _morph = new ExploreMorph(compositor, _graphics.Geometry, _camera);
        _labels = new ExploreLabels(_camera, _pictures.Root, SystemMotion.Current);
        _redraws = new CanvasRedraws(owner);
        _overview = new ExploreOverview(_camera, _zoom, SystemMotion.Current, () => _pointer);

        _pictures.Follow();

        // The outlines go over the picture, and the labels over both. A label is inset from its
        // shape's edge and an outline runs along it, so the two rarely meet — and where they do, the
        // name of the thing is worth more than the last pixel of the line round it. The picture a
        // change of folder is leaving is stacked against the new one as each step starts (see Swap).
        _root = compositor.CreateContainerVisual();
        _root.Children.InsertAtTop(_departing.Root);
        _root.Children.InsertAtTop(_pictures.Root);
        _root.Children.InsertAtTop(_shift.Root);
        _root.Children.InsertAtTop(_morph.Root);
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
            Children = { picture, _labels, _overview },
        };
        Content = _layers;

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

        // The overview is of the picture the map works from: the set of drawings a change of folder
        // leaves keeps its own, for a change turned round.
        _pictures.OverviewChanged += OnOverviewChanged;
        _departing.OverviewChanged += OnOverviewChanged;

        // A press on the overview moves the camera as any other input does, from a settled screen
        // with the whole picture under the zoomed one.
        _overview.Moving += (_, _) =>
        {
            SettleStep();
            Underlay();
        };
        _overview.Entered += (_, _) => FollowPointer();
        _overview.HiddenChanged += (_, _) => OverviewHiddenChanged?.Invoke(this, EventArgs.Empty);

        // The camera is held where it was, measured from its new origin, until this returns, so
        // everything on it is placed from that origin at once (see ExploreZoom.Rebase).
        _zoom.OriginMoved += (_, _) => Fit();

        // A pinch, a touch or the wheel with Ctrl held moves the picture on the compositor before the
        // map hears of it, so a change of folder still flying is settled and the whole picture painted under the zoomed one
        // as soon as it does, as they are for a move the map starts itself.
        _zoom.Started += (_, _) =>
        {
            SettleStep();
            Underlay();
        };

        _descent.Moved += OnDescentMoved;
        _descent.Midway += OnDescentMidway;
        _descent.Arrived += OnDescentArrived;
        _shift.Arrived += OnMoveArrived;
        _morph.Arrived += OnMoveArrived;

        // Both sets, as the two change places with each change of folder that flies (see Swap).
        _pictures.Faded += OnFaded;
        _departing.Faded += OnFaded;
        _pictures.Tiles.AnswersChanged += OnTileAnswersChanged;
        _departing.Tiles.AnswersChanged += OnTileAnswersChanged;

        _settled = DispatcherQueue.CreateTimer();
        _settled.Interval = ResizeSettleTime;
        _settled.IsRepeating = false;
        _settled.TickGuarded(Redraw);

        SizeChanged += OnSizeChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnDragEnded;
        PointerCaptureLost += OnDragEnded;
        PointerExited += OnPointerExited;
        PointerWheelChanged += OnPointerWheelChanged;
        DoubleTapped += OnDoubleTapped;
        KeyDown += OnKeyDown;
        PreviewKeyDown += OnPreviewKeyDown;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        ContextRequested += OnContextRequested;

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
            _capabilities.Changed += OnCapabilitiesChanged;
            SystemMotion.Current.Changed += OnMotionChanged;
            TintHighlight();
            _zoom.FollowMotion();

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
            // would rasterise a whole volume for a page that has been navigated away from. A change of
            // folder still flying, or a drag still held, is finished where it was going for the same reason.
            _settled.Stop();
            _zoom.Stop();
            SettleStep();
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
            _pictures.Light(null, null);
            _pictures.Trim();
            _departing.Tiles.Release();
            _buffers.Release();
            _graphics.Trim();

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
            _capabilities.Changed -= OnCapabilitiesChanged;
            SystemMotion.Current.Changed -= OnMotionChanged;
            _graphics.Replaced -= OnDeviceReplaced;
        };

        // The ground is baked into the canvas, so unlike every themed control around it the map
        // cannot restyle itself. Nothing else here fires on a theme switch — the page is kept alive
        // by NavigationCacheMode, so a trip to Settings and back does not rebuild it either — and
        // the map would keep the old ground until the window was resized. WindowBackdrop subscribes
        // this same event for the same reason.
        ActualThemeChanged += (_, _) => Redraw();

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
    /// the machine for it (see <see cref="MapShapeText.Help"/>). Null from a page that removes nothing,
    /// and for a tree it has nothing to say of, and then nothing is said either way.
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
        _zoom.Movable = Zoomable && _drawing is { Viewport: not null };
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
        if (sender == _pictures)
        {
            _overview.ShowWhole(_pictures.Overview);
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
            tree is null ? _ => string.Empty : NamedAndSized(tree),
            look,
            volume,
            viewport);

    /// <summary>What a shape of a scanned <paramref name="tree"/> is labelled with: its name, then its size.</summary>
    public static Func<int, string> NamedAndSized(ExploreTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return node => $"{tree.NameOf(node)}  {FreeSpace.Format(tree.SizeOf(node))}";
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
    /// <param name="labelText">What to write on a shape of the tree this drawing chose to label.</param>
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
        Func<int, string> labelText,
        MapLook look,
        VolumeSpace volume,
        MapViewport? viewport = null)
    {
        ArgumentNullException.ThrowIfNull(colours);
        ArgumentNullException.ThrowIfNull(labelText);
        ArgumentNullException.ThrowIfNull(look);

        // A zoom is into one picture. Another tree, another folder or another view is another
        // picture, and the part of this one the reader had magnified means nothing in it. So is the
        // same root with the volume beside it or without it, which is the root opened or closed. A
        // new colouring, scheme, spacing or finish is the same picture, so the zoom stays.
        var was = _zoom.Shown;
        var another = !ReferenceEquals(tree, _tree) || node != _node || view != _view || volume != _volume;

        // The same place in a new tree, drawn the same way: a scan's next snapshot, or what a removal
        // left. The same picture laid out again, so the zoom stays and the shapes move to where they
        // are now. A change of view, such as a finished scan drawn as a treemap after its snapshots'
        // icicles, is not. The volume beside it may have new figures, as a removal frees space, but
        // it is drawn beside both or beside neither.
        var samePlace = another
            && viewport is null
            && view == _view
            && (volume == VolumeSpace.None) == (_volume == VolumeSpace.None)
            && tree is not null
            && _tree is ITrackedTree shown
            && ExploreSurface.Drawn(tree, view) == ExploreSurface.Drawn(shown, _view)
            && shown.CounterpartsIn(tree)?.Invoke(_node) == node;
        var zooms = Zoomable && tree is not null && ExploreSurface.Zooms(tree, view);

        // The same place in another view: its largest shapes travel from where this view draws them
        // to where that one does, from the whole of one picture to the whole of the other.
        var morph = another
            && viewport is null
            && tree is not null
            && ReferenceEquals(tree, _tree)
            && node == _node
            && volume == _volume
            && ViewMorph.Morphs(tree, _view, view);

        // Drawn at the zoom asked for from the start, rather than drawn whole and then glided in.
        var opened = zooms && viewport is { } revisited ? revisited : MapViewport.Whole;

        MapFrame? into = null;

        if (another)
        {
            _picturesHanded++;
            EndDrag();

            if (_left is { Drawing: { } drawing } left
                && ReferenceEquals(tree, left.Tree) && node == left.Node && view == left.View && volume == left.Volume
                && opened == left.Viewport
                && Stepping)
            {
                Return(drawing, colours, labelText, look);
                return;
            }

            // Any other change of folder lands the one on its way first: the screen has room for the
            // flight between two pictures, and this one starts from the picture it arrived at.
            LandStep();

            var step = zooms && ReferenceEquals(tree, _tree) && view == _view && _drawing is { Viewport: not null }
                ? FolderSteps.Between(tree!, _node, _volume != VolumeSpace.None, node, volume != VolumeSpace.None)
                : FolderStep.Across;

            // A folder with nothing in it to draw has nothing to fly into, and the folder passed
            // through on the way would be the last thing seen.
            if (step == FolderStep.Through && tree!.SizeOf(node) <= 0)
            {
                step = FolderStep.Across;
            }

            // A flight through a folder starts as a step out to it, drawn whole as any folder opened is.
            _through = step == FolderStep.Through ? new Through(FolderSteps.Meeting(tree!, _node, node), opened) : null;
            if (!samePlace)
            {
                _zoom.Reset(_through is null ? opened : MapViewport.Whole);
            }

            // The picture on screen stays, to fly from, and the new one is drawn into the other set
            // of layers: over it for a step in, which the new picture grows out of, and under it for a
            // step out, which it shrinks back into.
            if (step != FolderStep.Across)
            {
                into = step == FolderStep.Into
                    ? _drawing!.ScreenOf(node, was, opened) ?? MapFrame.Whole
                    : null;

                _left = new Left(_tree!, _node, _view, _volume, was, _drawing, _shapeColours);
                _step = step == FolderStep.Into ? FolderStep.Into : FolderStep.OutOf;
                _awaitingOuter = _step == FolderStep.OutOf;

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
        if (another && !samePlace)
        {
            _pictures.ForgetOverview();
        }

        if (another)
        {
            CarryFocus(tree, node, volume != VolumeSpace.None, samePlace);
        }

        _tree = tree;
        _node = node;
        _view = view;
        _colours = colours;
        _labelText = labelText;
        _look = look;
        _volume = volume;

        // A step in starts before the new picture is drawn, so the drawing arrives into a flight
        // already on its way: its outlines wait for it the way they wait for a zoom, its names ride it in, and
        // the pointer is over nothing until the picture it is over is the one on screen. The names
        // of the picture left go with it. A step out flies once the picture it goes out to is drawn.
        if (_step != FolderStep.Across && another)
        {
            _labels.Hide();
            _highlight.Hide();

            if (into is { } shape)
            {
                _descent.Start(shape, FolderStep.Into);
            }
        }

        if (samePlace)
        {
            Shift();
        }
        else if (morph)
        {
            Morph();
        }
        else
        {
            Redraw();
        }

        // Nothing to fly to after all: a folder with nothing in it to draw.
        if (_step != FolderStep.Across && _drawing is null && _redraws.Pending is null)
        {
            LandStep();
        }

        // What the pointer is over and what is picked were read in the tree before, and are read in
        // this one now. The drawing on screen stays until the new one has arrived, and answers through
        // the counterparts of the shapes it draws where it is of the same place (MapReading); a change
        // of folder has none on screen, and the pointer is over nothing until it lands.
        if (another)
        {
            ShowPicked();
            ReportWhatThePointerIsOver();
        }

        // The same picture, zoomed to where the reader was, moves there as a zoom to a shape does.
        // After the redraw, which draws where the zoom is now and is the drawing the move starts from.
        if (!another && Zoomable && viewport is { } returning && tree is not null && ExploreSurface.Zooms(tree, view))
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
    /// Mark <paramref name="nodes"/> of <paramref name="tree"/> out on the picture as what a removal
    /// under way is acting on, and nothing once it is over.
    ///
    /// <para>Told, as the selection is, because what is being removed is the removal's to say: it is
    /// what was picked when the removal was asked for, which a pick made since does not change.</para>
    /// </summary>
    public void MarkRemoving(ISizedTree? tree, IReadOnlyList<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        _markedTree = tree;
        _marked.Clear();
        _marked.UnionWith(nodes);

        ShowMarked();
    }

    /// <summary>
    /// Light what a card beside the map points at, dimming the rest, or nothing where
    /// <paramref name="light"/> is null. See <see cref="MapLight"/>.
    ///
    /// <para>Told, as the selection is, because what the card points at is the card's to say. Kept and
    /// lit again on every new drawing, so it stays on the shapes it names through a zoom, a resize or a
    /// snapshot, as long as the page keeps it.</para>
    /// </summary>
    public void Light(MapLight? light)
    {
        _light = light;

        ShowLit();
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

        // Stopped and started rather than started, so each size change puts the whole wait back and
        // a drag that is still moving never reaches the end of one.
        _settled.Stop();
        _settled.Start();
    }

    /// <summary>
    /// Cut the picture at the map's edges while anything runs past them. The camera itself follows the
    /// tracker on the compositor (see <see cref="ExploreZoom"/>), so nothing here moves the picture.
    /// </summary>
    private void Place()
    {
        // Cut only while something can run past the edges. Unzoomed, nothing does except the halo of
        // an outline round a shape at the edge, which has always been drawn whole.
        var cut = !(_zoom.Shown.IsWhole && _drawn.IsWhole && _step == FolderStep.Across);

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
        _pictures.Fit(ActualWidth, ActualHeight, _zoom.Origin);

        if (_drawing is not { } drawing)
        {
            return;
        }

        _highlight.PlaceOver(
            _drawn.Canvas(drawing.Width, drawing.Height, ActualWidth, ActualHeight, _zoom.Origin),
            ActualWidth,
            ActualHeight,
            _zoom.Origin);

        _labels.Place(_drawn.Labels(drawing.Width, drawing.Height, ActualWidth, ActualHeight, _scale, _zoom.Origin));
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
        Redraw(SystemMotion.Current.For(MotionToken.Shift));

        // Owed only to a drawing on its way. A map that cannot be seen, or has nothing to draw, draws
        // nothing, and a drawing kept from before has already been shown.
        _shiftOwed = _redraws.Pending is not null;
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

        // Owed only to a drawing on its way, as a shift is, and only where the shapes travel. A swap at
        // once lands the new view in sight a region at a time, and what has landed answers the pointer
        // as it would for any other picture (§7.1).
        _morphOwed = motion.Travels && _redraws.Pending is not null;
    }

    private void Redraw() => Redraw(Motion.Instant);

    /// <param name="fade">How the new drawing comes in over the old. See <see cref="ExploreLayers.Arrival"/>.</param>
    private void Redraw(Motion fade)
    {
        // Whatever brought us here is more current than a size change still waiting to be drawn, or a
        // redraw stopped when the page was left; and a redraw for any other reason replaces a move
        // from the drawing on screen that was owed.
        _settled.Stop();
        _redrawOwed = false;
        _shiftOwed = false;
        _morphOwed = false;

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
            _hoveredTile = null;
            return;
        }

        _scale = XamlRoot?.RasterizationScale ?? 1;

        var width = DevicePixels(ActualWidth);
        var height = DevicePixels(ActualHeight);

        if (_tree is null || width <= 0 || height <= 0 || _tree.SizeOf(DrawnNode) <= 0)
        {
            // Every drawing goes, and the buffer they were painted through: a map with nothing to
            // show holds no memory for it.
            _redraws.Cancel();
            _pictures.Clear();
            _buffers.Release();
            _drawing = null;
            _shapeColours = null;
            _hovered = null;
            _hoveredTile = null;
            _labels.Clear();
            _highlight.Clear();
            Unlock();

            // There is no picture now, so nothing is under the pointer. Said rather than left: a
            // cancelled scan takes the tree away, and without this the line under the map goes on
            // naming whatever was last hovered over a blank canvas.
            Report(null);

            // A folder opened into nothing: the picture it opened out of goes too.
            if (_stepOwed)
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

        _contrast = Contrast();
        _shapeColours = _contrast is { } contrast ? ShapeColours.Contrasted(contrast) : _colours(DateTime.UtcNow);

        if (Contrasted != wasContrasted)
        {
            ContrastChanged?.Invoke(this, EventArgs.Empty);
        }

        // Every drawing kept is of the picture as it was, so none of them is shown again, though
        // they stay on screen until the new one has landed over them. Drawn at the zoom on screen
        // now, which is partway through a move if one is on its way: a repaint mid-move is placed
        // for the rest of the move like any other drawing.
        _pictures.Forget();

        Request(_zoom.Shown, fade);
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
        _pictures.Tiles.Rest();

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
            _pictures.Tiles.Hide();
            _labels.Reveal();
            Place();
            ReportWhatThePointerIsOver();
            return;
        }

        Request(_zoom.Shown, Motion.Instant);
    }

    /// <summary>
    /// Show <paramref name="viewport"/> of the picture: the drawing kept of it, at once, where there
    /// is one, and otherwise a redraw of it, which lands over the picture on screen.
    /// </summary>
    /// <param name="fade">How a new drawing comes in over the one on screen. See <see cref="ExploreLayers.Arrival"/>.</param>
    private void Request(MapViewport viewport, Motion fade)
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

        _arriving = Redraw(
            Layout(viewport, width, height),
            width,
            height,
            Focus(viewport, width, height),
            _pictures.Arrival(onTop: true, Present, fade));
    }

    /// <summary>
    /// Start a redraw of a drawing <paramref name="width"/> by <paramref name="height"/>, laid out by
    /// <paramref name="layout"/>: shaded on the GPU in one pass where the device can, and the drawing is of
    /// rectangles, and otherwise painted on the CPU a region at a time from <paramref name="focus"/> out.
    /// </summary>
    private CanvasRedraw Redraw(Func<ExploreSurface> layout, int width, int height, ExplorePoint? focus, ICanvasRedrawTarget target) =>
        _graphics.Shades && ExploreSurface.Drawn(_tree!, _view) != ExploreView.Sunburst
            ? _redraws.Shade(layout, Ground(), target)
            : _redraws.Start(layout, _buffers.PixelsFor(width, height), Ground(), focus, target);

    /// <summary>
    /// The folder drawn: the one asked for, or the one a change of folder is passing through on its
    /// way there. See <see cref="_through"/>.
    /// </summary>
    private int DrawnNode => _through?.Folder ?? _node;

    /// <summary>
    /// The volume drawn beside <see cref="DrawnNode"/>. None beside a folder passed through, which is
    /// drawn opened, as any folder the reader opens is.
    /// </summary>
    private VolumeSpace DrawnVolume => _through is null ? _volume : VolumeSpace.None;

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
        var node = DrawnNode;
        var view = _view;
        var scale = _scale;
        var textScale = SystemSettings.TextScaleFactor;
        var colours = _shapeColours!;
        var look = _look;
        var volume = DrawnVolume;

        return () => ExploreSurface.Create(
            tree, node, view, width, height, scale, textScale, colours, look.Spacing, volume, viewport, look.Style);
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
        var earlier = _drawing;

        _drawing = drawing;

        // A drawing that cannot be zoomed shows the whole picture whatever was asked, and the zoom
        // has to agree with it: the screen is placed, and every click resolved, by what was drawn
        // rather than by what was asked for (§7.1).
        if (drawing.Viewport is { } viewport)
        {
            _drawn = viewport;
            _zoom.Limit(drawing.Ceiling ?? throw new InvalidOperationException("A drawing that can be zoomed says how far."));
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
            _pictures.Tiles.Hide();
        }

        Unlock();

        // Over a change of folder still flying, too: the names ride the picture they name as it moves.
        _labels.Show(drawing, _scale, LabelText);

        // A screen reader's shapes are this drawing's now.
        (FrameworkElementAutomationPeer.FromElement(this) as MapAutomationPeer)?.Redrawn();

        // A new drawing is new geometry, so whatever was marked out is marked out somewhere else
        // now, and so is whatever the pointer is over.
        Fit();
        Place();
        ShowPicked();
        ShowLit();
        ShowMarked();
        ReportWhatThePointerIsOver();

        // A step out flies now that there is a picture to pull back into, from the shape the folder
        // left has in it. One that does not draw that folder's branch has nothing to fly from. A step
        // through the folder now drawn flies on into the folder asked for, from its shape in the same
        // drawing. A folder with no shape in it fades on its leg rather than flies (see MapPassage).
        if (_awaitingOuter)
        {
            _awaitingOuter = false;

            if (_left is { } passed && _through is { } through)
            {
                _descent.Pass(
                    drawing.ScreenOf(passed.Node, _zoom.Shown, passed.Viewport),
                    drawing.ScreenOf(_node, _zoom.Shown, through.Opened));
            }
            else if (_left is { } left && drawing.ScreenOf(left.Node, _zoom.Shown, left.Viewport) is { } shape)
            {
                _descent.Start(shape, FolderStep.OutOf);
            }
            else
            {
                FinishStep();
            }
        }

        // A flight in that arrived before its picture did finishes now, over it.
        if (_stepOwed && !_descent.IsMoving)
        {
            FinishStep();
        }

        if (_shiftOwed)
        {
            _shiftOwed = false;
            MoveFrom(earlier, drawing);
            Follow(earlier, drawing);
        }

        if (_morphOwed)
        {
            _morphOwed = false;
            MorphFrom(earlier, drawing);
        }

        // The whole picture under a zoomed drawing only shows once the picture moves, so it waits
        // until the drawing the reader asked for is on screen. Asked again as a move starts, for a
        // move that comes first.
        if (drawing.Viewport is { IsWhole: false })
        {
            DispatcherQueue.TryEnqueueGuarded(DispatcherQueuePriority.Low, Underlay);
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

        Redraw(
            Layout(MapViewport.Whole, drawing.Width, drawing.Height),
            drawing.Width,
            drawing.Height,
            focus: null,
            _pictures.Arrival(onTop: false, static _ => { }, Motion.Instant));
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
        var spot = _pointer is { } pointer ? Locate(pointer) : null;
        var hit = spot?.Hit;

        _hoveredTile = spot?.Tile;
        Shine(spot);

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
    ///
    /// <para>In a high contrast theme, the theme's own window colour, which the shapes are filled with too.</para>
    private TileColour Ground() => _contrast is { } contrast
        ? contrast.Window
        : ActualTheme == ElementTheme.Dark
            ? new TileColour(32, 32, 32)
            : new TileColour(243, 243, 243);

    /// <summary>
    /// The colours of the high contrast theme Windows is in, or null where it is in none. Read from the
    /// system rather than the application's resources, as <see cref="TintHighlight"/> reads them.
    /// </summary>
    private static ContrastColours? Contrast()
    {
        if (!HighContrast.IsEnabled())
        {
            return null;
        }

        var window = SystemSettings.UIElementColor(UIElementType.Window);
        var text = SystemSettings.UIElementColor(UIElementType.WindowText);

        return new ContrastColours(new TileColour(window.R, window.G, window.B), new TileColour(text.R, text.G, text.B));
    }

    /// <summary>
    /// What to write on a labelled shape. The blocks standing for the rest of the volume are the
    /// map's own, because they are not nodes of the page's tree.
    /// </summary>
    private string LabelText(ExploreLabel label) => label.Node >= 0
        ? _labelText(label.Node)
        : $"{MapShapeText.BlockName(label.Node)}  {FreeSpace.Format(label.Bytes)}";

    /// <summary>Draw the outline round whatever is picked and this drawing actually drew.</summary>
    private void ShowPicked()
    {
        if (Outlining is not { } outlining)
        {
            _highlight.ShowPicked([], 0);
            return;
        }

        var drawing = outlining.Drawing;

        // Twice the whole picture in the drawing's canvas pixels, which is a canvas as many times
        // over as the drawing is zoomed: the picture is within that of the canvas's corner whichever
        // part of it the canvas shows, and so is the margin the outlines are drawn into round it.
        var far = 2 * Math.Max(drawing.Width, drawing.Height) * (drawing.Viewport?.Zoom ?? 1);

        _highlight.ShowPicked(drawing.Outlines(outlining.Reading.InDrawing(_picked)), (float)far);
    }

    /// <summary>
    /// Light what a card beside the map is pointing at, in the drawing the outlines are drawn in, and
    /// nothing while that drawing is of another tree or a change of folder is still on its way.
    /// </summary>
    private void ShowLit() =>
        _highlight.ShowLit(_light is { } light && Outlining is { } outlining ? light.On(outlining.Drawing) : null);
    /// <summary>
    /// The drawing the outlines are drawn in, which is the one the map works from, with how it is read
    /// in the tree the page handed over last. What is picked is named in that tree, and the same
    /// numbers in an older one are other shapes, so a drawing of an older tree of the same place,
    /// still on screen while the new one lands, marks out each node where it draws the node's
    /// counterpart (<see cref="MapReading"/>). A drawing of anything else marks nothing out.
    /// </summary>
    private (ExploreSurface Drawing, MapReading Reading)? Outlining =>
        _drawing is { } drawing && ReadingOf(drawing.Tree) is { } reading ? (drawing, reading) : null;

    /// <summary>
    /// How a drawing of <paramref name="drawn"/> is read in the tree the page handed over last, or null
    /// where it cannot be. Found once for each pair of trees, because finding nodes again builds an
    /// index of each folder it looks in.
    /// </summary>
    private MapReading? ReadingOf(ISizedTree drawn)
    {
        if (_reading is not { } reading || !ReferenceEquals(reading.Drawn, drawn) || !ReferenceEquals(reading.Current, _tree))
        {
            _reading = MapReading.Of(drawn, _tree);
        }

        return _reading;
    }

    /// <summary>
    /// Draw the accent outline round whatever the pointer is over.
    ///
    /// <para>A shape already picked gets nothing, because it would carry two outlines, leaving the
    /// weaker claim on top of the stronger one. Nor does a block that stands for something other than
    /// a node, which cannot be picked (§7.1).</para>
    ///
    /// <para>The shape the keyboard is on is outlined the same way, while the keyboard has the map,
    /// blocks too: a reader moving with the arrow keys has to see where they are, and a block is where
    /// they can be.</para>
    /// </summary>
    private void ShowHovered()
    {
        _under.Clear();

        if (_hovered is { IsNode: true } hit
            && !_picked.Contains(hit.Node))
        {
            _under.Add(hit.Node);
        }

        if (Outlining is not { } outlining)
        {
            _highlight.ShowHovered([], null, Vector2.Zero);
            return;
        }

        var drawing = outlining.Drawing;
        var focused = FocusOutlines(drawing);

        // Found on a tile, the shape may be one the drawing does not draw, so the tile outlines it, in
        // the drawing's canvas. Nothing is lifted: the drawing's surface holds coarser pixels there.
        if (_hoveredTile is { } tile && drawing.Viewport is { } drawn)
        {
            _highlight.ShowHovered(
                [
                    .. _under.Count > 0 && ReadingOf(tile.Picture.Tree) is { } inTile
                        ? tile.Outlines(inTile.InDrawing(_under), drawn, drawing.Width, drawing.Height)
                        : [],
                    .. focused,
                ],
                null,
                new Vector2(drawing.Width, drawing.Height));
            return;
        }

        _highlight.ShowHovered(
            [.. _under.Count > 0 ? drawing.Outlines(outlining.Reading.InDrawing(_under)) : [], .. focused],
            _pictures.SurfaceOf(drawing),
            new Vector2(drawing.Width, drawing.Height));
    }

    /// <summary>
    /// The outline of the shape the keyboard is on, while the keyboard has the map and the shape is
    /// neither picked nor under the pointer, which mark it already; and none otherwise. Only in a drawing
    /// of the tree the page handed over last, which is the one the keyboard's nodes are numbers in.
    /// </summary>
    private IReadOnlyList<ExploreOutline> FocusOutlines(ExploreSurface drawing)
    {
        if (!_keyboard || !ReferenceEquals(drawing, Readable) || _focus.Find(drawing) is not { } found)
        {
            return [];
        }

        var shape = found.Shapes[found.Index];

        return _picked.Contains(shape.Node) || _under.Contains(shape.Node) ? [] : drawing.OutlineOf(shape);
    }

    /// <summary>
    /// Hatch what a removal under way is acting on, in the drawing on screen while it is of the tree
    /// those nodes are numbers in: in any other tree the same numbers are other shapes (§7.1).
    /// </summary>
    private void ShowMarked() =>
        _highlight.ShowMarked(
            _marked.Count > 0 && _drawing is { } drawing && ReferenceEquals(drawing.Tree, _markedTree)
                ? drawing.Outlines(_marked)
                : [],
            _scale);

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
    private ExploreHit? At(Point point) => Locate(point)?.Hit;

    /// <summary>
    /// Where <paramref name="point"/>, in the control's own coordinates, falls in the drawing that
    /// shows there, or null where none does. See <see cref="At"/>.
    /// </summary>
    private MapLocation? Locate(Point point) =>
        ActualWidth <= 0 || ActualHeight <= 0 || Stepping || _overview.Covers(point)
            ? null
            : MapHitTest.Locate(
                _zoom.Shown, point.X / ActualWidth, point.Y / ActualHeight, _drawing,
                _shiftOwed || _morphOwed ? null : _arriving,
                _readingOf,
                _tileOver);

    /// <summary>
    /// Whether the screen is between two pictures: a change of folder flying or waiting for the
    /// picture it is to fly out to, shapes moving to a new layout, or a new drawing fading in.
    /// </summary>
    private bool Stepping => _descent.IsMoving || _awaitingOuter || _shift.IsMoving || _morph.IsMoving || _pictures.IsFading;

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
        if (!_zoom.Movable || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var delta = point.Properties.MouseWheelDelta;

        SettleStep();
        Underlay();

        _pointer = point.Position;
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
    /// Move between the shapes with the arrow keys, on the focused map, and move the picture with the
    /// rest: plus and minus zoom about the middle, the arrows with Shift pan, and Home shows the whole
    /// picture. A key with Alt or Ctrl held is left alone, because Alt+Left and Alt+Right step back and
    /// forward. On the overview's buttons, which are inside the map, the arrows pan as they always have.
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Held(VirtualKey.Menu) || Held(VirtualKey.Control))
        {
            return;
        }

        if (ReferenceEquals(e.OriginalSource, this) && !Held(VirtualKey.Shift) && DirectionOf(e.Key) is { } direction)
        {
            e.Handled = MoveFocus(direction);
            return;
        }

        if (!_zoom.Movable || KeyOf(e.Key) is not { } key)
        {
            return;
        }

        SettleStep();
        Underlay();

        _zoom.Press(key);

        e.Handled = true;
    }

    /// <summary>
    /// Enter opens the shape the keyboard is on, and Backspace goes up a folder, on the map itself. Taken
    /// before the page's own keys for them, which open what is picked and go back.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, this) || Held(VirtualKey.Menu) || Held(VirtualKey.Control) || Held(VirtualKey.Shift))
        {
            return;
        }

        if (e.Key == VirtualKey.Enter && _keyboard && Readable is { } drawing && FocusedShape() is { } focused && ShapeOf(focused) is { } shape)
        {
            OpenShape(drawing, focused.Folder, shape);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Back && Ascending is { } ascending)
        {
            ascending(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private static bool Held(VirtualKey modifier) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(modifier).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Which way <paramref name="key"/> moves between the shapes, or null where it is not an arrow.</summary>
    private static MapDirection? DirectionOf(VirtualKey key) => key switch
    {
        VirtualKey.Left => MapDirection.Left,
        VirtualKey.Right => MapDirection.Right,
        VirtualKey.Up => MapDirection.Up,
        VirtualKey.Down => MapDirection.Down,
        _ => null,
    };

    /// <summary>What <paramref name="key"/> does to the camera, or null where it does nothing.</summary>
    private static MapKey? KeyOf(VirtualKey key) => key switch
    {
        VirtualKey.Add or PlusKey => MapKey.ZoomIn,
        VirtualKey.Subtract or MinusKey => MapKey.ZoomOut,
        VirtualKey.Left => MapKey.Left,
        VirtualKey.Right => MapKey.Right,
        VirtualKey.Up => MapKey.Up,
        VirtualKey.Down => MapKey.Down,
        VirtualKey.Home => MapKey.Whole,
        _ => null,
    };

    /// <summary>The main keyboard's plus and minus (VK_OEM_PLUS, VK_OEM_MINUS), which VirtualKey has no names for.</summary>
    private const VirtualKey PlusKey = (VirtualKey)0xBB;

    private const VirtualKey MinusKey = (VirtualKey)0xBD;

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

        // Not ReportWhatThePointerIsOver, which marks the shape out again whether or not it changed.
        // That is right once per new drawing and wrong at every frame of a move over the same one:
        // the outline is in the drawing's own pixels and moves with the placement, and redrawing it
        // is a pass over every shape and two new geometries, sixty times a second.
        FollowPointer();
    }

    /// <summary>
    /// Show the tiles of the picture the screen shows as the camera moves, and draw those it is missing,
    /// from the one under the pointer outwards (see <see cref="ExploreTiles"/>): what comes into view is
    /// as sharp as the zoom it is seen at, rather than the drawing worked from magnified until the move
    /// stops. Not while a change of folder flies or new shapes travel, which move a picture on its way
    /// to being another one.
    /// </summary>
    private void Sharpen()
    {
        if (Stepping || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0 || _pictures.Tiled is not { } picture)
        {
            return;
        }

        var shown = _zoom.Shown;
        var (x, y) = _pointer is { } pointer
            ? shown.PictureAt(pointer.X / ActualWidth, pointer.Y / ActualHeight)
            : shown.PictureAt(0.5, 0.5);

        _pictures.Tiles.Want(picture, shown, _zoom.Target, x, y, Ground());
    }

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

        _tapPress = point.PointerDeviceType != PointerDeviceType.Mouse || point.Properties.IsLeftButtonPressed;

        // The pointer has the map now: the shape the keyboard was on is not marked until a key moves again.
        if (_keyboard)
        {
            _keyboard = false;
            ShowHovered();
        }
        _earlierPressLandedMorph = _pressLandedMorph;
        _pressLandedMorph = _morph.IsMoving;

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

        _pointer = position;

        if (ActualWidth > 0
            && ActualHeight > 0
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

        FollowPointer();
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

    /// <summary>
    /// Say what is under the pointer, and mark it out, where that is not what was already under it.
    /// </summary>
    private void FollowPointer()
    {
        if (_pointer is not { } pointer)
        {
            return;
        }

        var spot = Locate(pointer);
        var hit = spot?.Hit;
        var tile = spot?.Tile;

        Shine(spot);

        // Only when it changed. A pointer moves at the display's refresh rate and lands on the same
        // shape for most of that, so reporting every move would rebuild the same string sixty times
        // a second. The same shape found on another tile, or on the drawing, is outlined again from
        // there, and not reported again.
        if (hit == _hovered && tile == _hoveredTile)
        {
            return;
        }

        var moved = hit != _hovered;

        _hovered = hit;
        _hoveredTile = tile;

        ShowHovered();

        if (moved)
        {
            Report(hit);
        }
    }

    /// <summary>Say what the pointer found.</summary>
    private void Report(ExploreHit? hit) => Hovered?.Invoke(this, hit);

    /// <summary>
    /// Put the pointer's light where <paramref name="spot"/> is, or take it away: see
    /// <see cref="PointerLight"/>.
    ///
    /// <para>Only on the drawing at rest on screen, where it was shaded on the GPU, and only while effects
    /// are fast. Not on a tile, which a move shows for a moment, nor on a drawing on its way in, and not in
    /// high contrast, where the picture is the theme's and nothing is added to it (§6.5).</para>
    /// </summary>
    private void Shine(MapLocation? spot)
    {
        if (spot is { Tile: null } found
            && ReferenceEquals(found.Drawing, _drawing)
            && _graphics.Shades
            && !HighContrast.IsEnabled())
        {
            _pictures.Light(found.Drawing, PointerLight.At(found.X, found.Y, _scale));
        }
        else
        {
            _pictures.Light(null, null);
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
            _pictures.Tiles.Release();
            _departing.Tiles.Release();
            _graphics.Trim();
        }

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

        LandStep();
        _redraws.Cancel();
        _departing.Renew();
        _pictures.Renew();

        _drawing = null;
        _hovered = null;
        _hoveredTile = null;
        _labels.Hide();
        _highlight.Clear();
        Report(null);

        Redraw();
    }

    /// <summary>
    /// Draw again for a new text size, because the bands and the label thresholds are sized for the
    /// old one. Raised off the UI thread, so it is sent back to it.
    /// </summary>
    private void OnTextScaleChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueueGuarded(Redraw);

    /// <summary>
    /// Follow a change of accent colour, or high contrast turned on or off or to another theme, which
    /// draws the picture again in the colours it now has. Raised off the UI thread, like the text size
    /// above.
    /// </summary>
    private void OnSystemColoursChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueueGuarded(() =>
        {
            TintHighlight();

            if (Contrast() != _contrast)
            {
                Redraw();
            }
        });

    /// <summary>Follow the compositor's word on whether effects are fast, which a remote session changes.</summary>
    private void OnCapabilitiesChanged(CompositionCapabilities sender, object args) =>
        DispatcherQueue.TryEnqueueGuarded(TintHighlight);

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
        // The overview's mark in white over a dark halo, which reads over any colour in the picture,
        // or in high contrast the theme's own colour for what is selected, over its window colour.
        if (HighContrast.IsEnabled())
        {
            _overview.Restyle(SystemSettings.UIElementColor(UIElementType.Highlight), SystemSettings.UIElementColor(UIElementType.Window));
        }
        else
        {
            _overview.Restyle(Microsoft.UI.Colors.White, Windows.UI.Color.FromArgb(170, 0, 0, 0));
        }

        _highlight.Restyle(
            SystemSettings.GetColorValue(UIColorType.AccentLight2),
            HighContrast.IsEnabled()
                ? new SystemHighlight(
                    SystemSettings.UIElementColor(UIElementType.Window),
                    SystemSettings.UIElementColor(UIElementType.Highlight),
                    SystemSettings.UIElementColor(UIElementType.Hotlight))
                : null,
            _capabilities.AreEffectsFast());
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointer = null;
        _hovered = null;
        _hoveredTile = null;

        Shine(null);
        ShowHovered();
        Report(null);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_tapPress && !_pressLandedMorph && !_drag.Dragged)
        {
            Pick(e.GetPosition(this));
        }
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // A picture that answers nothing has nothing to offer a menu for (§7.1).
        if (!MapReading.Answers(_tree) || _pressLandedMorph)
        {
            return;
        }

        var point = e.GetPosition(this);

        Pick(point);
        MenuRequested?.Invoke(this, point);
    }

    /// <summary>
    /// Say what is at <paramref name="point"/>. Neither block picks anything. The one standing in
    /// for items too small to draw is several thousand files at once, and §7.1 has no bulk action,
    /// and free space is not on the disk to be acted on. A picture that answers nothing, such as a
    /// step of a replay of what grew (<see cref="MapReading.Answers"/>), says nothing, so a click on it
    /// leaves the selection as it is rather than clearing it.
    /// </summary>
    private void Pick(Point point)
    {
        if (!MapReading.Answers(_tree))
        {
            return;
        }

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
    /// <para>The page is what decides whether a node opens, and it says so by showing it here before
    /// <see cref="Activated"/> returns. The camera then flies into the folder's shape, as it does for
    /// every step into a folder however the page asked for it (see <see cref="Show(ISizedTree, int, ExploreView, Func{DateTime, ShapeColours}, Func{int, string}, MapLook, VolumeSpace, MapViewport?)"/>).
    /// The root of a scan of a whole volume opens the same way, out of the volume drawn beside it.</para>
    ///
    /// <para>What does not open, a file, the block standing in for items too small to draw, or the
    /// block standing for free space, is zoomed to until it fills the map as far as its shape allows.
    /// For the block of small items that is how they come to be drawn one by one.</para>
    /// </summary>
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(this);

        if (!_tapPress || _pressLandedMorph || _earlierPressLandedMorph || _drag.Dragged || Locate(point) is not { } spot || spot.Hit is not { } hit)
        {
            return;
        }

        // Only a treemap nests: an icicle draws what a shape holds below it rather than in it.
        Open(spot.Drawing, hit, spot.Drawing.Viewport is null ? null : spot.ShapeOnScreen(_zoom.Shown));
    }

    /// <summary>
    /// Open <paramref name="hit"/>, drawn in <paramref name="drawing"/> where <paramref name="shape"/>
    /// is on the screen, and zoom to it where it cannot be opened: what a double-click does, and Enter on
    /// the shape the keyboard is on. See <see cref="OnDoubleTapped"/>.
    /// </summary>
    private void Open(ExploreSurface drawing, ExploreHit hit, MapFrame? shape)
    {
        // The shape the whole drawing is of opens only out of what is drawn beside it: the volume's
        // free space, beside the root of a treemap of a whole drive. Anywhere else it is already
        // open, and asking the page to open it again would be asking for the picture on screen.
        if (hit.IsNode && (hit.Node != _node || drawing.HasVolumeBeside))
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

        if (Zoomable && shape is { } frame)
        {
            var fitting = MapViewport.Fitting(_zoom.Shown.PictureOf(frame), _zoom.Ceiling);

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
    /// The drawing the keyboard and a screen reader work from: the one the map works from, while it is
    /// of the tree the page handed over last, which the page's facts, the selection and the keyboard's
    /// shape are numbered in. Null while there is none, or one of an older tree is still on screen.
    /// </summary>
    internal ExploreSurface? Readable => _drawing is { } drawing && ReferenceEquals(drawing.Tree, _tree) ? drawing : null;

    /// <summary>Whether what is picked on this picture is acted on: not on a step of a replay (<see cref="MapReading.Answers"/>).</summary>
    internal bool Answers => MapReading.Answers(_tree);

    /// <summary>
    /// The shapes of <paramref name="folder"/>, or of the top of the picture for null, that a screen
    /// reader is given: none where the page has given no facts to describe them with.
    /// </summary>
    internal IReadOnlyList<MapShape> ShapesIn(int? folder) =>
        Readable is { } drawing && FactsOf(drawing, DrawnNode) is not null ? drawing.Shapes(folder) : [];

    private MapShapeFacts? FactsOf(ExploreSurface drawing, int node) => Facts?.Invoke(drawing.Tree, node);

    /// <summary>The folder whose shapes hold <paramref name="node"/>'s, or null for the top of the picture. See <see cref="MapFolders.Of"/>.</summary>
    internal int? FolderOf(int node) =>
        Readable is { } drawing ? MapFolders.Of(drawing.Tree, DrawnNode, drawing.HasVolumeBeside, node) : null;

    /// <summary>The shapes a screen reader is given under <paramref name="folder"/>'s own shape, none while it is collapsed. See <see cref="MapFolders.Listed"/>.</summary>
    internal IReadOnlyList<MapShape> ListedIn(int folder) =>
        Readable is { } drawing && FactsOf(drawing, DrawnNode) is not null
            ? MapFolders.Listed(drawing, DrawnNode, folder, _focus.Folder, _expanded)
            : [];

    /// <summary>Whether a screen reader is given <paramref name="folder"/>'s shapes. See <see cref="MapFolders.Lists"/>.</summary>
    internal bool Lists(int folder) =>
        Readable is { } drawing && MapFolders.Lists(drawing.Tree, DrawnNode, drawing.HasVolumeBeside, folder, _focus.Folder, _expanded);

    /// <summary>The folder whose shapes the keyboard moves between, or null for the top of the picture.</summary>
    internal int? KeyboardFolder => _focus.Folder;

    /// <summary>A screen reader expanded <paramref name="folder"/>. False where it was already.</summary>
    internal bool Expand(int folder) => _expanded.Add(folder);

    /// <summary>A screen reader collapsed <paramref name="folder"/>. False where it had not expanded it.</summary>
    internal bool Collapse(int folder) => _expanded.Remove(folder);

    /// <summary>
    /// What a screen reader calls <paramref name="shape"/>: its name, its size and its share of what
    /// holds it, which at the top of a volume's picture is the drive. See <see cref="MapShapeText.Name"/>.
    /// </summary>
    internal string NameOf(int? folder, MapShape shape)
    {
        if (Readable is not { } drawing)
        {
            return string.Empty;
        }

        var name = shape.IsNode ? FactsOf(drawing, shape.Node)?.Name : MapShapeText.BlockName(shape.Node);
        var (ofBytes, named) = MapFolders.ShareBasis(drawing.Tree, DrawnNode, drawing.HasVolumeBeside, folder, drawing.Shapes(null));

        // A share of the drive is named by no node. A share of a folder the page says nothing of is not
        // said at all, rather than said to be of the drive.
        return (name, named) switch
        {
            (null, _) => string.Empty,
            (_, null) => MapShapeText.Name(name, shape.Bytes, ofBytes, ofName: null),
            (_, { } folderNode) when FactsOf(drawing, folderNode) is { } of => MapShapeText.Name(name, shape.Bytes, ofBytes, of.Name),
            _ => MapShapeText.Name(name, shape.Bytes, ofBytes: 0, ofName: null),
        };
    }

    /// <summary>What <paramref name="shape"/> is and what can be done with it. See <see cref="MapShapeText.Help"/>.</summary>
    internal string HelpOf(MapShape shape) =>
        !shape.IsNode ? MapShapeText.BlockHelp(shape.Node)
        : Readable is { } drawing && FactsOf(drawing, shape.Node) is { } facts ? MapShapeText.Help(facts, Removal?.Invoke(drawing.Tree, shape.Node))
        : string.Empty;

    /// <summary>Where <paramref name="shape"/> is on the screen now, in fractions of the map, or null where there is no drawing to say.</summary>
    internal MapFrame? OnScreenOf(MapShape shape) =>
        Readable is { } drawing ? PlacementOn(_zoom.Shown, drawing).OnScreen(shape.Frame) : null;

    /// <summary>The shape the keyboard is on, while it has the map, and null otherwise.</summary>
    internal (int? Folder, int Node)? FocusedShape() =>
        _keyboard && FocusState != FocusState.Unfocused && _focus.Node is { } node ? (_focus.Folder, node) : null;

    internal bool HasFocusOn(int? folder, int node) => FocusedShape() == (folder, node);

    internal bool IsPicked(int node) => _picked.Contains(node);

    /// <summary>What is picked, each with the folder whose shapes hold it.</summary>
    internal IEnumerable<(int? Folder, int Node)> PickedShapes() => _picked.Select(node => (FolderOf(node), node));

    /// <summary>
    /// Put the keyboard on <paramref name="shape"/> of <paramref name="drawing"/>, as a screen reader
    /// asks: the map takes the focus if it has not got it. Nothing where that drawing is no longer the
    /// one on screen, whose node numbers may be other nodes now (§7.1).
    /// </summary>
    internal void FocusShape(ExploreSurface drawing, int? folder, MapShape shape)
    {
        if (!ReferenceEquals(drawing, Readable))
        {
            return;
        }

        _keyboard = true;
        _focus.Set(drawing, folder, shape);

        if (FocusState == FocusState.Unfocused)
        {
            Focus(FocusState.Programmatic);
        }

        Focused(drawing, folder, shape, pick: false);
    }

    /// <summary>
    /// Pick <paramref name="shape"/> of <paramref name="drawing"/> and put the keyboard on it, as a screen
    /// reader selecting it asks: the same pick a click makes, which Delete and the menu then act on
    /// (§7.1). Nothing where that drawing is no longer the one on screen: a peer a screen reader kept
    /// from an earlier drawing names a node number that may be another node in the tree on screen now.
    /// </summary>
    internal void PickShape(ExploreSurface drawing, int? folder, MapShape shape)
    {
        if (!ReferenceEquals(drawing, Readable))
        {
            return;
        }

        _keyboard = true;
        Focused(drawing, folder, shape, pick: true);
    }

    /// <summary>Pick nothing, as a click on empty space does.</summary>
    internal void Unpick()
    {
        if (Answers)
        {
            Picked?.Invoke(this, null);
        }
    }

    /// <summary>Bring <paramref name="shape"/> of <paramref name="drawing"/> into view, as a screen reader scrolling to it asks, while that drawing is on screen.</summary>
    internal void BringIntoView(ExploreSurface drawing, MapShape shape)
    {
        if (ReferenceEquals(drawing, Readable))
        {
            Bring(drawing, shape);
        }
    }

    /// <summary>
    /// Open <paramref name="shape"/> of <paramref name="drawing"/>, or zoom to it where it cannot be
    /// opened: what Enter and a double-click do. Nothing where that drawing is no longer on screen.
    /// </summary>
    internal void OpenShape(ExploreSurface drawing, int? folder, MapShape shape)
    {
        SettleStep();

        if (!ReferenceEquals(drawing, Readable))
        {
            return;
        }

        _focus.Set(drawing, folder, shape);

        Open(
            drawing,
            new ExploreHit(shape.Node, shape.Bytes),
            drawing.Viewport is null ? null : PlacementOn(_zoom.Shown, drawing).OnScreen(shape.Frame));
    }

    /// <summary>The shape the keyboard is on, as the drawing on screen lists it, or null where it does not.</summary>
    private MapShape? ShapeOf((int? Folder, int Node) focused)
    {
        foreach (var shape in Readable?.Shapes(focused.Folder) ?? [])
        {
            if (shape.Node == focused.Node)
            {
                return shape;
            }
        }

        return null;
    }

    /// <summary>The one node picked, which the keyboard starts on where it is at the top of the picture.</summary>
    private int? PickedAlone => _picked.Count == 1 ? _picked.First() : null;

    /// <summary>Where <paramref name="drawing"/> lies on a screen showing <paramref name="shown"/>.</summary>
    private static MapPlacement PlacementOn(MapViewport shown, ExploreSurface drawing) =>
        shown.PlacementOf(drawing.Viewport ?? MapViewport.Whole);

    /// <summary>
    /// Move the keyboard to the shape next to the one it is on in <paramref name="direction"/>, and pick
    /// it, as a list's arrow keys pick the row they reach; and stay where there is none that way. Starts
    /// from the shape picked where the keyboard was not moving. False where there is no drawing with
    /// shapes to move between, so the key goes on to the page.
    /// </summary>
    private bool MoveFocus(MapDirection direction)
    {
        SettleStep();

        // From what a click picked, where the keyboard was not already moving.
        if (!_keyboard && PickedAlone is { } picked)
        {
            _focus.Expect(FolderOf(picked), picked);
        }

        if (Readable is not { } drawing || _focus.Find(drawing) is not { } found)
        {
            return false;
        }

        // The first key onto a map nothing is picked on marks the shape it starts on, rather than leave it.
        var moves = _keyboard || PickedAlone is not null;
        var next = moves ? MapNeighbours.Toward(found.Shapes, found.Index, direction, drawing.Width, drawing.Height) : null;

        _keyboard = true;
        Focused(drawing, _focus.Folder, found.Shapes[next ?? found.Index], pick: next is not null);

        return true;
    }

    /// <summary>
    /// The keyboard is on <paramref name="shape"/> of <paramref name="folder"/>: it is marked, the line
    /// under the map names it, the camera brings it into view, and a screen reader hears it. Picked as
    /// well where <paramref name="pick"/> says, a block picking nothing as a click on one does.
    /// </summary>
    private void Focused(ExploreSurface drawing, int? folder, MapShape shape, bool pick)
    {
        _focus.Set(drawing, folder, shape);

        if (pick && Answers)
        {
            Picked?.Invoke(this, shape.IsNode ? shape.Node : null);
        }

        ShowHovered();
        Report(new ExploreHit(shape.Node, shape.Bytes));
        Bring(drawing, shape);

        if (FrameworkElementAutomationPeer.FromElement(this) is not MapAutomationPeer peer)
        {
            return;
        }

        // Into another folder: the shapes a screen reader is given follow the keyboard's path.
        peer.KeyboardMoved();

        if (AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged) && peer.PeerOf(folder, shape.Node) is { } focused)
        {
            focused.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
        }
    }

    /// <summary>
    /// Move the camera as little as brings <paramref name="shape"/> into view at a size it can be seen at
    /// (<see cref="MapViewport.Showing"/>): big enough to carry its name, at the reader's text size.
    /// </summary>
    private void Bring(ExploreSurface drawing, MapShape shape)
    {
        if (!_zoom.Movable || drawing.Viewport is not { } drawn || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var text = SystemSettings.TextScaleFactor;
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

        _keyboard = true;

        if (_focus.Node is null && PickedAlone is { } picked)
        {
            _focus.Expect(FolderOf(picked), picked);
        }

        if (Readable is { } drawing && _focus.Find(drawing) is { } found)
        {
            Focused(drawing, _focus.Folder, found.Shapes[found.Index], pick: false);
        }
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, this))
        {
            _keyboard = false;
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

        var picked = PickedAlone;

        if (!_keyboard)
        {
            if (picked is not { } clicked)
            {
                return;
            }

            _focus.Expect(FolderOf(clicked), clicked);
        }

        if (_focus.Find(drawing) is not { } found || (!_keyboard && found.Shapes[found.Index].Node != picked))
        {
            return;
        }

        var shape = found.Shapes[found.Index];

        _keyboard = true;
        Focused(drawing, _focus.Folder, shape, pick: true);

        var placed = PlacementOn(_zoom.Target, drawing);

        MenuRequested?.Invoke(this, new Point(
            Math.Clamp(placed.X + (shape.X * placed.Scale), 0, 1) * ActualWidth,
            Math.Clamp(placed.Y + (shape.Y * placed.Scale), 0, 1) * ActualHeight));

        e.Handled = true;
    }

    /// <summary>
    /// Where the keyboard is in the picture handed over: on the same shapes in a new tree of the same
    /// place, read through their counterparts; on the folder it came out of, in a folder above it; on the
    /// root, where the root is drawn again beside its volume; and on the first shape of anything else.
    /// </summary>
    private void CarryFocus(ISizedTree? tree, int node, bool beside, bool samePlace)
    {
        if (samePlace && _tree is ITrackedTree shown && tree is not null && shown.CounterpartsIn(tree) is { } counterpart)
        {
            _focus.Carry(counterpart);

            int[] expanded = [.. _expanded.Select(counterpart).OfType<int>()];

            _expanded.Clear();
            _expanded.UnionWith(expanded);
            return;
        }

        // Another place is another picture, whose folders a reader has not expanded yet.
        _expanded.Clear();

        if (tree is not null && ReferenceEquals(tree, _tree) && MapFolders.CameUpFrom(tree, _node, node, beside) is { } left)
        {
            _focus.Expect(null, left);
            return;
        }

        _focus.Clear();
    }

    /// <summary>
    /// Hand the set of layers on screen to the picture being left, held where the camera had it at
    /// <paramref name="left"/>, and draw the new picture into the other set, which follows the camera.
    /// </summary>
    private void Swap(MapViewport left)
    {
        (_pictures, _departing) = (_departing, _pictures);

        _departing.Freeze(left.Camera(ActualWidth, ActualHeight, _zoom.Origin));
        _pictures.Follow();
        _pictures.Fit(ActualWidth, ActualHeight, _zoom.Origin);
        _labels.Ride(_pictures.Root);
        _overview.ShowWhole(_pictures.Overview);

        // The inner picture goes over the outer one: a folder opened grows over the picture it was
        // in, and a folder left shrinks back into the picture it is in. The outlines stay on top.
        _root.Children.Remove(_departing.Root);

        if (_step == FolderStep.OutOf)
        {
            _root.Children.InsertAbove(_departing.Root, _pictures.Root);
        }
        else
        {
            _root.Children.InsertAtBottom(_departing.Root);
        }
    }

    /// <summary>
    /// The reader asked for the folder the flight is leaving before it arrived, at the zoom they left it
    /// at: the flight turns round from where it is, at the speed it has, and the drawing left, still on
    /// screen, is the one the map works from again. Nothing is drawn afresh, and nothing jumps.
    /// </summary>
    private void Return(ExploreSurface drawing, Func<DateTime, ShapeColours> colours, Func<int, string> labelText, MapLook look)
    {
        var left = _left!;
        var leaving = _zoom.Shown;

        _redraws.Cancel();

        // The folder being left is the one drawn: on the way out of a step through a folder, the
        // folder passed through, which the drawing kept is of.
        _left = new Left(_tree!, DrawnNode, _view, DrawnVolume, leaving, _drawing, _shapeColours);
        _step = _step == FolderStep.Into ? FolderStep.OutOf : FolderStep.Into;
        _through = null;
        _awaitingOuter = false;
        _stepOwed = false;

        _zoom.Reset(left.Viewport);
        Swap(leaving);

        _tree = left.Tree;
        _node = left.Node;
        _view = left.View;
        _volume = left.Volume;
        _colours = colours;
        _labelText = labelText;
        _look = look;
        _shapeColours = left.Colours;

        // A step out still waiting for its picture had not moved yet: there is nothing to turn round.
        var flying = _descent.IsMoving;

        _descent.Turn();
        Present(drawing);

        if (!flying)
        {
            FinishStep();
        }
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
        _shift.Finish();
        _morph.Finish();
        _pictures.LandFades();

        if (_descent.IsMoving)
        {
            _descent.Finish();
        }
        else if (_awaitingOuter)
        {
            if (_through is { } through)
            {
                _through = null;
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

        if (_step != FolderStep.Across)
        {
            FinishStep();
        }
    }

    /// <summary>
    /// One frame of a change of folder: the outer picture stretched so the inner folder's shape fills
    /// more of the map or less, and the inner folder's own drawing over it on the same frame, coming in
    /// or going.
    /// </summary>
    private void OnDescentMoved(object? sender, EventArgs e)
    {
        var (inner, outer) = _step == FolderStep.OutOf ? (_departing, _pictures) : (_pictures, _departing);

        outer.Carry(_descent.Outer, 1, ActualWidth, ActualHeight);
        inner.Carry(_descent.Inner, _descent.Opacity, ActualWidth, ActualHeight);
    }

    /// <summary>
    /// Move from <paramref name="earlier"/>, the drawing that was on screen, to <paramref name="later"/>,
    /// the same place laid out again, which has just arrived (<see cref="ShapeChanges"/>). Only between
    /// two drawings of one canvas at one zoom, whose shapes are where the same rectangles of the screen
    /// say. The pointer is over nothing while the shapes travel, as it is during a change of folder:
    /// a shape is not where it is drawn until it lands (§7.1).
    /// </summary>
    private void MoveFrom(ExploreSurface? earlier, ExploreSurface later)
    {
        var motion = SystemMotion.Current.For(MotionToken.Shift);

        if (!motion.Travels
            || earlier is null
            || earlier.Width != later.Width
            || earlier.Height != later.Height
            || earlier.Viewport != later.Viewport
            || _pictures.SurfaceOf(earlier) is not { } before
            || _pictures.SurfaceOf(later) is not { } after)
        {
            return;
        }

        var travels = ShapeChanges.Between(earlier, later)
            .Select(change => ShapeTravel.Of(change, later.Width, later.Height))
            .OfType<ShapeTravel>()
            .ToList();

        if (travels.Count == 0)
        {
            return;
        }

        _shift.Start(travels, before, after, _drawn.Canvas(later.Width, later.Height, ActualWidth, ActualHeight, _zoom.Origin), motion);
        _labels.Hide();
        _highlight.Hide();
        ReportWhatThePointerIsOver();
    }

    /// <summary>
    /// Carry the largest shapes from <paramref name="earlier"/>, the drawing that was on screen, into
    /// <paramref name="later"/>, the same place in another view, which has just arrived
    /// (<see cref="ViewMorph"/>). The pointer is over nothing while they travel, and a press lands
    /// them and picks nothing: no shape is where either view puts it (§7.1).
    /// </summary>
    private void MorphFrom(ExploreSurface? earlier, ExploreSurface later)
    {
        var motion = SystemMotion.Current.For(MotionToken.Morph);

        if (!motion.Travels || earlier is null)
        {
            return;
        }

        var morphs = ViewMorph.Between(earlier, later);

        if (morphs.Count == 0)
        {
            return;
        }

        _morph.Start(morphs, later.Width, later.Height, _drawn.Canvas(later.Width, later.Height, ActualWidth, ActualHeight, _zoom.Origin), motion);
        _labels.Hide();
        _highlight.Hide();
        ReportWhatThePointerIsOver();
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
        if (!_zoom.Movable
            || earlier is null
            || later.Viewport is not { } drawn
            || _zoom.Shown != drawn
            || _zoom.Target != drawn)
        {
            return;
        }

        var motion = SystemMotion.Current.For(MotionToken.Shift);

        // With animation effects off nothing travels, and the camera goes straight to where the shape is.
        _zoom.MoveTo(MapFollow.Onward(earlier, later), motion.Travels ? motion : Motion.Instant);
    }

    /// <summary>A new drawing has finished fading in: the pointer is over what it shows.</summary>
    private void OnFaded(object? sender, EventArgs e) => ReportWhatThePointerIsOver();

    /// <summary>
    /// A tile has come in under a pointer that has not moved, or one it was over has gone: the pointer
    /// is over what the screen shows now (§7.1).
    /// </summary>
    private void OnTileAnswersChanged(object? sender, EventArgs e) => ReportWhatThePointerIsOver();

    /// <summary>
    /// The shapes have landed, from a new layout or into a new view: the names, the outlines and the
    /// readout are the new drawing's again.
    /// </summary>
    private void OnMoveArrived(object? sender, EventArgs e)
    {
        _labels.Reveal();
        _highlight.Reveal();
        ReportWhatThePointerIsOver();
    }

    /// <summary>
    /// A flight through a folder has reached the picture of the folder that holds both ends, which
    /// fills the screen: from here it is a step into the folder asked for, out of that picture. The
    /// folder left goes, the picture passed through is held where it is to fly out of, and the folder
    /// asked for is drawn into the other set of layers, arriving into the flight as a step in's does.
    /// </summary>
    private void OnDescentMidway(object? sender, EventArgs e)
    {
        var through = _through!;
        var passing = _zoom.Shown;

        _left = new Left(_tree!, through.Folder, _view, VolumeSpace.None, passing, _drawing, _shapeColours);
        _through = null;
        _step = FolderStep.Into;

        // The set the folder left was in takes the folder asked for.
        _departing.Clear();
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
        if (_drawing is null && _redraws.Pending is not null)
        {
            _stepOwed = true;
            return;
        }

        FinishStep();
    }

    /// <summary>The end of a change of folder, once its flight is over and its picture has arrived.</summary>
    private void FinishStep()
    {
        _step = FolderStep.Across;
        _left = null;
        _through = null;
        _awaitingOuter = false;
        _stepOwed = false;

        _departing.Clear();
        _departing.Carry(MapFrame.Whole, 1, ActualWidth, ActualHeight);
        _pictures.Carry(MapFrame.Whole, 1, ActualWidth, ActualHeight);

        // Marked out again for the drawing worked from now, before they show: one settled before its
        // picture arrived has none, and the hatch and outlines hidden with the picture left belong to
        // its shapes, which in this picture are other shapes or none (§7.1).
        ShowPicked();
        ShowLit();
        ShowMarked();

        _labels.Reveal();
        _highlight.Reveal();
        Place();
        ShowOverview();
        ReportWhatThePointerIsOver();
    }

    /// <summary>
    /// The folder a change of folder left, as it was on screen: the zoom it was at, the drawing the map
    /// worked from, if one had arrived, and the colours its drawings were made in.
    /// </summary>
    private sealed record Left(
        ISizedTree Tree,
        int Node,
        ExploreView View,
        VolumeSpace Volume,
        MapViewport Viewport,
        ExploreSurface? Drawing,
        ShapeColours? Colours);

    /// <summary>
    /// The folder a change of folder through a folder is passing through, and the zoom the folder
    /// asked for is shown at once the flight has passed it.
    /// </summary>
    private sealed record Through(int Folder, MapViewport Opened);
}
