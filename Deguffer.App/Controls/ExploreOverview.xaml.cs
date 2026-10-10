using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The overview a zoomed map shows in a corner: a small copy of the whole picture, made once as the
/// whole drawing arrives (see <see cref="ExploreLayers.Overview"/>), and a rectangle round the part
/// of it on the screen.
///
/// <para>The rectangle follows the map's camera by an expression, so it moves with every pinch,
/// fling and glide on the compositor with nothing written as it does. Pressed, the overview moves
/// the camera: the rules are Core's (<see cref="MapOverviewPress"/>), and this hears the pointer and
/// asks the camera (G1).</para>
///
/// <para>It takes every press, tap and menu request made on it, so none of them reaches the map
/// under it to pick a shape the reader cannot see (§7.1). The map asks <see cref="Covers"/> for the
/// same answer when it says what the pointer is over.</para>
/// </summary>
public sealed partial class ExploreOverview
{
    /// <summary>The property holding the picture's size in the overview, in device-independent pixels.</summary>
    private const string Size = "Size";

    /// <summary>The property holding <see cref="MapOverview.LeastFrame"/>.</summary>
    private const string Least = "Least";

    private readonly ExploreZoom _zoom;

    private readonly IMotionPolicy _motion;

    private readonly MapOverviewPress _press = new();

    private readonly Visual _visual;

    private readonly CompositionSurfaceBrush _brush;

    private readonly SpriteVisual _picture;

    private readonly ShapeVisual _frame;

    private readonly CompositionColorBrush _halo;

    private readonly CompositionColorBrush _line;

    /// <summary>The picture's size in the overview, which the rectangle's expression reads.</summary>
    private readonly CompositionPropertySet _properties;

    private MapCorner _corner = MapCorner.TopRight;

    /// <summary>Whether the map wants the overview on screen: zoomed in, with a picture of the place on screen.</summary>
    private bool _wanted;

    /// <summary>Whether it is on screen, or on its way: see <see cref="Present"/>.</summary>
    private bool _shown;

    internal ExploreOverview(MapCamera camera, ExploreZoom zoom, IMotionPolicy motion)
    {
        InitializeComponent();

        _zoom = zoom;
        _motion = motion;
        _visual = ElementCompositionPreview.GetElementVisual(this);

        var compositor = _visual.Compositor;

        _properties = compositor.CreatePropertySet();
        _properties.InsertVector2(Size, Vector2.Zero);
        _properties.InsertScalar(Least, (float)MapOverview.LeastFrame);

        _brush = compositor.CreateSurfaceBrush();
        _brush.Stretch = CompositionStretch.Fill;

        _picture = compositor.CreateSpriteVisual();
        _picture.Brush = _brush;
        _picture.RelativeSizeAdjustment = Vector2.One;

        _halo = compositor.CreateColorBrush();
        _line = compositor.CreateColorBrush();

        var bounds = compositor.CreateRectangleGeometry();
        var halo = compositor.CreateSpriteShape(bounds);
        halo.StrokeBrush = _halo;
        halo.StrokeThickness = 3.5f;
        var line = compositor.CreateSpriteShape(bounds);
        line.StrokeBrush = _line;
        line.StrokeThickness = 1.5f;

        _frame = compositor.CreateShapeVisual();
        _frame.RelativeSizeAdjustment = Vector2.One;
        _frame.Shapes.Add(halo);
        _frame.Shapes.Add(line);

        Follow(camera, bounds);

        // Cut to the picture, so a drag stretched past the picture's edge takes the rectangle as far
        // as the overview's edge and no further.
        var root = compositor.CreateContainerVisual();
        root.RelativeSizeAdjustment = Vector2.One;
        root.Clip = compositor.CreateInsetClip();
        root.Children.InsertAtTop(_picture);
        root.Children.InsertAtTop(_frame);
        ElementCompositionPreview.SetElementChildVisual(Picture, root);

        Picture.SizeChanged += (_, e) =>
            _properties.InsertVector2(Size, new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));

        ElementCompositionPreview.SetIsTranslationEnabled(this, true);
        _visual.Opacity = 0;
        Visibility = Visibility.Collapsed;

        // Every press, tap and menu request on it is the overview's, wherever on it they land: none of
        // them is for the shape under it (§7.1). Moves and wheel turns go on to the map, which says the
        // pointer is over nothing here, and zooms where the wheel turned.
        PointerPressed += (_, e) => e.Handled = true;
        PointerReleased += (_, e) => e.Handled = true;
        Tapped += (_, e) => e.Handled = true;
        DoubleTapped += (_, e) => e.Handled = true;
        RightTapped += (_, e) => e.Handled = true;
    }

    /// <summary>Raised before the overview moves the camera, so the map can settle what it must first.</summary>
    public event EventHandler? Moving;

    /// <summary>Raised when the reader hides the overview or brings it back.</summary>
    public event EventHandler? HiddenChanged;

    /// <summary>
    /// Whether the reader hid the overview, which leaves a button in its corner that brings it back.
    /// The page keeps the choice (see <c>AppPreferences.ExploreOverviewHidden</c>).
    /// </summary>
    public bool Hidden
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            Present();
        }
    }

    /// <summary>Whether the overview, or the button standing for it, is on screen or on its way there.</summary>
    public bool IsShown => _shown;

    /// <summary>Show <paramref name="overview"/>, a small copy of the whole picture, or nothing while there is none.</summary>
    public void ShowWhole(ICompositionSurface? overview)
    {
        _brush.Surface = overview;
        Present();
    }

    /// <summary>
    /// The map is <paramref name="width"/> by <paramref name="height"/> now. The overview has its
    /// shape, and none where it is too small for one (see <see cref="MapOverview.Size"/>).
    /// </summary>
    public void Fit(double width, double height)
    {
        if (MapOverview.Size(width, height) is { } size)
        {
            Frame.Width = size.Width;
            Frame.Height = size.Height;
        }
        else
        {
            Frame.Width = 0;
            Frame.Height = 0;
        }

        Margin = new Thickness(MapOverview.Margin);
        Present();
    }

    /// <summary>
    /// Whether the map wants the overview: zoomed in, with no change of folder flying. It shows once
    /// it also has a picture and room, and goes the moment any of them goes.
    /// </summary>
    public void Want(bool wanted)
    {
        if (_wanted == wanted)
        {
            return;
        }

        _wanted = wanted;
        Present();
    }

    /// <summary>
    /// The map is being acted on at <paramref name="point"/> of a map <paramref name="width"/> by
    /// <paramref name="height"/>: a wheel turned, or a drag holds the picture there. Where that is
    /// near the overview it comes in at the other corner instead, so it never covers what is acted on.
    /// </summary>
    public void Avoid(Point point, double width, double height)
    {
        var corner = MapOverview.Avoiding(_corner, point.X, point.Y, width, height);

        if (corner == _corner)
        {
            return;
        }

        _corner = corner;
        HorizontalAlignment = corner == MapCorner.TopRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        if (_shown)
        {
            Enter();
        }
    }

    /// <summary>
    /// Whether the overview, or the button that brings it back, is at <paramref name="point"/> of the
    /// map, and takes the pointer there. Nothing on the map is under the pointer where it is (§7.1).
    /// </summary>
    public bool Covers(Point point)
    {
        if (!_shown)
        {
            return false;
        }

        var offset = ActualOffset;
        var size = ActualSize;

        return point.X >= offset.X && point.X < offset.X + size.X && point.Y >= offset.Y && point.Y < offset.Y + size.Y;
    }

    /// <summary>
    /// Draw the rectangle in <paramref name="line"/> over a halo of <paramref name="halo"/>, so it reads
    /// over any colour in the picture.
    /// </summary>
    public void Restyle(Color line, Color halo)
    {
        _line.Color = line;
        _halo.Color = halo;
    }

    /// <summary>
    /// Put the rectangle round the part of the picture on the screen, from the camera, on the
    /// compositor: <see cref="MapOverview.Frame"/>'s arithmetic, which a press is resolved through, so
    /// the two must stay the same.
    /// </summary>
    private void Follow(MapCamera camera, CompositionRectangleGeometry bounds)
    {
        var across = $"Max(1 / camera.Scale.X, overview.{Least} / overview.{Size}.X)";
        var down = $"Max(1 / camera.Scale.Y, overview.{Least} / overview.{Size}.Y)";
        var left = $"({MapCamera.ShownEdge("X")} + (0.5 / camera.Scale.X) - ({across} / 2))";
        var top = $"({MapCamera.ShownEdge("Y")} + (0.5 / camera.Scale.Y) - ({down} / 2))";

        bounds.StartAnimation(nameof(CompositionRectangleGeometry.Offset), Expression($"Vector2({left} * overview.{Size}.X, {top} * overview.{Size}.Y)"));
        bounds.StartAnimation(nameof(CompositionRectangleGeometry.Size), Expression($"Vector2({across} * overview.{Size}.X, {down} * overview.{Size}.Y)"));

        ExpressionAnimation Expression(string expression)
        {
            var animation = _visual.Compositor.CreateExpressionAnimation(expression);
            animation.SetReferenceParameter("camera", camera.Properties);
            animation.SetReferenceParameter("overview", _properties);

            return animation;
        }
    }

    /// <summary>
    /// Bring the overview, or the button standing for it, on screen or take it off, as the map wants,
    /// as the reader chose, and as there is a picture and room for one.
    /// </summary>
    private void Present()
    {
        var shown = _wanted && _brush.Surface is not null && Frame.Width > 0;

        Frame.Visibility = Hidden ? Visibility.Collapsed : Visibility.Visible;
        ShowButton.Visibility = Hidden ? Visibility.Visible : Visibility.Collapsed;

        if (shown == _shown)
        {
            return;
        }

        _shown = shown;

        if (!shown)
        {
            _press.Release();
            Leave();
            return;
        }

        Enter();
    }

    /// <summary>
    /// Come in where the overview stands: dropping a short way into place as it fades in, or with motion
    /// off fading in place. It takes the pointer at once, so a press on what is coming in is its own.
    /// </summary>
    private void Enter()
    {
        var motion = _motion.For(MotionToken.Overview);
        var compositor = _visual.Compositor;

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;

        _visual.StopAnimation(nameof(Visual.Opacity));
        _visual.StopAnimation("Translation");

        if (motion.IsInstant)
        {
            _visual.Opacity = 1;
            return;
        }

        var (first, second) = Motion.EaseControlPoints;
        var ease = compositor.CreateCubicBezierEasingFunction(first, second);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = motion.Duration;
        _visual.StartAnimation(nameof(Visual.Opacity), fade);

        if (motion.Travels)
        {
            var rise = compositor.CreateVector3KeyFrameAnimation();
            rise.InsertKeyFrame(0, new Vector3(0, -8, 0));
            rise.InsertKeyFrame(1, Vector3.Zero, ease);
            rise.Duration = motion.Duration;
            _visual.StartAnimation("Translation", rise);
        }
    }

    /// <summary>
    /// Fade out, taking the pointer no longer from the moment it starts to go, and leave the page's
    /// tab order once it has gone.
    /// </summary>
    private void Leave()
    {
        var motion = _motion.For(MotionToken.Overview);
        var compositor = _visual.Compositor;

        IsHitTestVisible = false;
        _visual.StopAnimation("Translation");
        _visual.StopAnimation(nameof(Visual.Opacity));

        if (motion.IsInstant)
        {
            _visual.Opacity = 0;
            Visibility = Visibility.Collapsed;
            return;
        }

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0);
        fade.Duration = motion.Duration;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _visual.StartAnimation(nameof(Visual.Opacity), fade);
        batch.End();

        batch.Completed += (_, _) =>
        {
            // Come back while it went: the entrance has it now.
            if (!_shown)
            {
                Visibility = Visibility.Collapsed;
            }
        };
    }

    /// <summary>
    /// Hide the overview. The button just pressed has gone, so the corner keeps the focus, on the button
    /// that brings it back, as the page's notes do, whichever way the toggle went.
    /// </summary>
    private void OnHideClicked(object sender, RoutedEventArgs e)
    {
        Hidden = true;
        HiddenChanged?.Invoke(this, EventArgs.Empty);
        ShowButton.Focus(FocusState.Programmatic);
    }

    private void OnShowClicked(object sender, RoutedEventArgs e)
    {
        Hidden = false;
        HiddenChanged?.Invoke(this, EventArgs.Empty);
        HideButton.Focus(FocusState.Programmatic);
    }

    /// <summary>A press with the left button, a touch or a pen: the camera stops where it is, and the press is the overview's.</summary>
    private void OnPicturePressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Picture);

        e.Handled = true;

        if (!_shown || (point.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed))
        {
            return;
        }

        Picture.CapturePointer(e.Pointer);

        Moving?.Invoke(this, EventArgs.Empty);
        _zoom.Halt();
        _press.Press(point.Position.X, point.Position.Y, Picture.ActualWidth, Picture.ActualHeight, _zoom.Shown, _zoom.Ceiling);
    }

    private void OnPictureMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_press.IsHeld)
        {
            return;
        }

        var point = e.GetCurrentPoint(Picture);
        var held = point.IsInContact && (point.PointerDeviceType != PointerDeviceType.Mouse || point.Properties.IsLeftButtonPressed);

        if (_press.Move(point.Position.X, point.Position.Y, held) is { } target)
        {
            _zoom.JumpTo(target);
        }
    }

    private void OnPictureReleased(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;

        var target = _press.Release();

        Picture.ReleasePointerCaptures();

        if (target is { } flight)
        {
            Moving?.Invoke(this, EventArgs.Empty);
            _zoom.GlideTo(flight);
        }
    }

    /// <summary>The pointer was taken away mid-press: whatever it had moved stays, and nothing flies.</summary>
    private void OnPictureLost(object sender, PointerRoutedEventArgs e) => _press.Release();
}
