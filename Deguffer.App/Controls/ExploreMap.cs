using System.Runtime.InteropServices.WindowsRuntime;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// Draws a scanned tree, and says what the pointer is over.
///
/// <para>The geometry is one bitmap. A full volume lays out to tens of thousands of shapes, and the
/// framework's own performance guidance is that a vector element repeated enough times should
/// become an image instead — which is also what every reference implementation does, WinDirStat
/// rendering into a cached surface and blitting it rather than keeping a shape per file.</para>
///
/// <para>The labels are not in the bitmap. They are real text controls laid over it, so they scale
/// with the user's text size and a screen reader can read them — none of which text burnt into a
/// bitmap offers. There are only ever a few dozen, because a shape too small to read is a shape
/// with no label.</para>
///
/// <para>Nothing here knows what a drive is, how one is scanned, or how any of the views are laid
/// out. It is handed a tree, a node and a view, it asks Core for the matching
/// <see cref="ExploreSurface"/>, and it reports back what was pointed at (G1).</para>
/// </summary>
public sealed class ExploreMap : UserControl
{
    /// <summary>
    /// How long a run of size changes has to stop for before the map is drawn again.
    ///
    /// <para>Dragging a window edge raises <see cref="FrameworkElement.SizeChanged"/> tens of times
    /// a second, and each one is a whole layout of the tree, a fresh bitmap, and a pass over every
    /// pixel of it — at 3840 by 2160 that is eight million pixels shaded several times over. Drawing
    /// each of those in turn is not merely repeated work: it is work for a size that was superseded
    /// before the paint finished, so the window falls further behind the pointer the longer the drag
    /// goes on.</para>
    ///
    /// <para>So the size changes are coalesced and only the size the user settles on is drawn. In
    /// between, the <see cref="Image"/> stretches the bitmap it already has over the new bounds,
    /// which is the right picture at the wrong scale and is on screen with no work at all.</para>
    /// </summary>
    private static readonly TimeSpan ResizeSettleTime = TimeSpan.FromMilliseconds(120);

    private readonly Image _surface = new()
    {
        // The bitmap is rendered at the display's pixel size and stretched back over the control's
        // logical size, so this maps one bitmap pixel to one device pixel rather than resampling.
        Stretch = Stretch.Fill,
    };

    private readonly Canvas _labels = new()
    {
        // Labels sit over the map and are never the thing being clicked: a click that landed on a
        // label rather than the shape under it would select whatever the label happened to overlap.
        IsHitTestVisible = false,
    };

    private readonly DispatcherQueueTimer _settled;

    private ExploreTree? _tree;
    private int _node;
    private ExploreView _view = ExploreView.Treemap;
    private ExploreColouring _colouring = ExploreColouring.Branch;
    private ExploreSurface? _drawing;
    private WriteableBitmap? _bitmap;
    private byte[]? _pixels;
    private double _scale = 1;
    private ExploreHit? _hovered;

    public ExploreMap()
    {
        Content = new Grid { Children = { _surface, _labels } };

        _settled = DispatcherQueue.CreateTimer();
        _settled.Interval = ResizeSettleTime;
        _settled.IsRepeating = false;
        _settled.Tick += (_, _) => Redraw();

        SizeChanged += OnSizeChanged;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        DoubleTapped += OnDoubleTapped;

        // Picking is separate from opening, on the file-manager idiom: one click says which one, two
        // says go in. A right-click picks as well, so the menu that follows is about the shape under
        // the pointer rather than about whatever was picked last.
        Tapped += OnTapped;
        RightTapped += OnRightTapped;

        // Dragged to a differently scaled display, the control's size in device-independent units
        // does not change, so nothing above fires and the bitmap stays at the old resolution.
        // The scale is read from the XamlRoot rather than from this element: the identically
        // documented property on UIElement does not update on a scale change
        // (microsoft-ui-xaml #9610), and the root is not attached until this is loaded.
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root)
            {
                root.Changed += OnRootChanged;
            }

            // A resize that arrived while this was on screen, and was still waiting to be drawn
            // when the page was navigated away from, is dropped below rather than rasterised for a
            // page nobody is looking at. Coming back at that same size raises no SizeChanged, so
            // this is the only place that owes the redraw — and without it the map stays stretched
            // over the old canvas for as long as the page is open.
            if (_drawing is { } drawing
                && (drawing.Width != DevicePixels(ActualWidth)
                    || drawing.Height != DevicePixels(ActualHeight)))
            {
                Redraw();
            }
        };

        Unloaded += (_, _) =>
        {
            // A pending redraw for a size this control is no longer showing at. Left running it
            // would rasterise a whole volume for a page that has been navigated away from.
            _settled.Stop();

            // The labels go back with it. Dropping the redraw drops the thing that would have put
            // them back, and Loaded only redraws where the size has actually moved — so without
            // this a map returned to at the size it was last drawn at comes back with no names on
            // it at all, and nothing on the page would put them there.
            _labels.Visibility = Visibility.Visible;

            if (XamlRoot is { } root)
            {
                root.Changed -= OnRootChanged;
            }
        };

        // The ground is baked into the bitmap, so unlike every themed control around it the map
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
        AutomationProperties.SetHelpText(
            this,
            "A picture of what is using the space. Choose List in the View box for the same "
            + "contents as a readable list.");
    }

    /// <summary>The node the user asked to open, by double-clicking a shape.</summary>
    public event EventHandler<int>? Activated;

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
    /// What the pointer moved over: a node, or a byte count where it is over the block standing in
    /// for items too small to draw. Both null when it is over nothing.
    /// </summary>
    public event EventHandler<(int? Node, long? AggregateBytes)>? Hovered;

    /// <summary>
    /// Draw <paramref name="node"/> of <paramref name="tree"/> in <paramref name="view"/>, with the
    /// shapes coloured to say <paramref name="colouring"/>.
    /// </summary>
    public void Show(ExploreTree? tree, int node, ExploreView view, ExploreColouring colouring)
    {
        _tree = tree;
        _node = node;
        _view = view;
        _colouring = colouring;

        Redraw();
    }

    /// <summary>
    /// Wait for the drag to stop before redrawing, on the terms
    /// <see cref="ResizeSettleTime"/> gives.
    ///
    /// <para>The first size the control is ever given is drawn at once. There is no bitmap to
    /// stretch in the meantime, so deferring that one would leave the panel empty for the length of
    /// the wait every time the page is opened.</para>
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_bitmap is null)
        {
            Redraw();
            return;
        }

        // The bitmap stretches with the control and the labels do not, because they are real
        // controls at fixed positions rather than part of the picture. Left visible they would sit
        // over whichever shape had moved under them, naming it wrongly. They come back with the
        // layout that puts them where they belong.
        _labels.Visibility = Visibility.Collapsed;

        // Stopped and started rather than started, so each size change puts the whole wait back and
        // a drag that is still moving never reaches the end of one.
        _settled.Stop();
        _settled.Start();
    }

    private void Redraw()
    {
        // Whatever brought us here is more current than a size change still waiting to be drawn.
        _settled.Stop();

        // Nothing can see it, and the page asks again as it brings the map back. ExplorePage
        // collapses this for the List view and calls Show() in the same breath, so without this a
        // switch to List rasterises a whole volume into a control nobody is looking at — and the
        // zero-size path below would then drop the bitmap and the buffer, so switching back
        // allocates 33 MB of both again (G5).
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        _hovered = null;

        _scale = XamlRoot?.RasterizationScale ?? 1;

        var width = DevicePixels(ActualWidth);
        var height = DevicePixels(ActualHeight);

        if (_tree is not { } tree || width <= 0 || height <= 0 || tree.SizeOf(_node) <= 0)
        {
            // The bitmap goes with the source. Keeping it would let the reuse below match on size
            // and never reattach it, leaving the control blank until the window is resized.
            _surface.Source = null;
            _bitmap = null;
            _pixels = null;
            _drawing = null;
            _labels.Children.Clear();
            return;
        }

        // The clock is read here rather than held, because the age bands are relative to now and
        // a map left on screen overnight would otherwise keep yesterday's answer. A repaint costs
        // one read of it against a full rasterisation.
        var drawing = ExploreSurface.Create(
            tree, _node, _view, width, height, _scale, _colouring, DateTime.UtcNow);
        _drawing = drawing;

        // Both reused while the size holds. A scan redraws this every three quarters of a second,
        // and at 3840 by 2160 the buffer alone is 33 MB of large-object-heap allocation — several
        // megabytes of garbage per second, for a surface whose dimensions only change when the
        // window does (G5).
        if (_bitmap is not { } bitmap || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
        {
            bitmap = new WriteableBitmap(width, height);
            _bitmap = bitmap;
            _pixels = new byte[PixelBuffer.LengthFor(width, height)];
            _surface.Source = bitmap;
        }

        drawing.Paint(_pixels!, Ground());

        _pixels!.CopyTo(0, bitmap.PixelBuffer, 0, _pixels!.Length);
        bitmap.Invalidate();

        DrawLabels(tree, drawing);
    }

    /// <summary>
    /// How many bitmap pixels a control extent of <paramref name="extent"/> device-independent
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
    /// Lay the labels over the finished bitmap, where the surface said they go.
    ///
    /// <para>Which shapes are worth labelling is the drawing's decision and is made in Core, where
    /// it can be tested. What is left here is the part that is a control: the text, the colour, and
    /// the turn a sunburst's labels take to lie along their own ring.</para>
    ///
    /// <para>The text blocks are kept and written over rather than rebuilt. A scan repaints this
    /// several times a second, and each rebuild would throw away a few dozen controls and their
    /// brushes and make the framework measure and arrange a fresh set of them, for text that has
    /// usually not changed (G5).</para>
    /// </summary>
    private void DrawLabels(ExploreTree tree, ExploreSurface drawing)
    {
        _labels.Visibility = Visibility.Visible;

        while (_labels.Children.Count < drawing.Labels.Count)
        {
            _labels.Children.Add(NewLabel());
        }

        while (_labels.Children.Count > drawing.Labels.Count)
        {
            _labels.Children.RemoveAt(_labels.Children.Count - 1);
        }

        for (var i = 0; i < drawing.Labels.Count; i++)
        {
            var label = drawing.Labels[i];
            var text = (TextBlock)_labels.Children[i];

            text.Text = $"{tree.NameOf(label.Node)}  {FreeSpace.Format(tree.SizeOf(label.Node))}";
            text.TextAlignment = label.Centred ? TextAlignment.Center : TextAlignment.Left;
            text.Width = label.Width / _scale;

            ((SolidColorBrush)text.Foreground).Color = Color.FromArgb(
                255, label.Colour.Red, label.Colour.Green, label.Colour.Blue);

            ((RotateTransform)text.RenderTransform).Angle = label.Rotation;

            Canvas.SetLeft(text, label.X / _scale);
            Canvas.SetTop(text, label.Y / _scale);
        }
    }

    /// <summary>
    /// One reusable piece of label text, with everything a repaint never changes already set —
    /// including the brush and the transform, which are written through rather than replaced.
    /// </summary>
    private static TextBlock NewLabel()
    {
        var text = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(),

            // Zero for anything in rectangles, and per label for a sunburst, which turns each one to
            // lie along its own ring. Always present rather than attached only where it turns: a
            // transform of no degrees costs nothing to keep, and a branch here would mean a label
            // reused from a sunburst kept its angle on a treemap.
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };

        // Announced through the list view instead: a label here duplicates a row there, and a
        // screen reader reading fifty fragments of a picture helps nobody.
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);

        return text;
    }

    /// <summary>
    /// What is under <paramref name="point"/>, in the control's own coordinates.
    ///
    /// <para>Mapped through the drawing's own dimensions rather than through the display scale,
    /// because the two part company for as long as a resize is still settling. The bitmap is
    /// stretched over the control's new bounds in the meantime, so what is on screen is the old
    /// canvas scaled to fit, and a pointer mapped by the display scale would answer from geometry
    /// that is no longer where it is drawn.</para>
    ///
    /// <para>§7.1 makes that a safety question rather than a cosmetic one. A right-click picks
    /// what the menu then acts on, so a pick that disagrees with the picture is a Delete aimed at
    /// something the user never pointed at — the same mistake <c>ExplorePage</c> avoids by picking
    /// from the row under the pointer rather than from the last selection.</para>
    /// </summary>
    private ExploreHit? At(ExploreSurface drawing, Point point) =>
        ActualWidth > 0 && ActualHeight > 0
            ? drawing.At(
                (float)(point.X * drawing.Width / ActualWidth),
                (float)(point.Y * drawing.Height / ActualHeight))
            : null;

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drawing is not { } drawing)
        {
            return;
        }

        var hit = At(drawing, e.GetCurrentPoint(this).Position);

        // Only when it changed. A pointer moves at the display's refresh rate and lands on the same
        // shape for most of that, so reporting every move would rebuild the same string sixty times
        // a second.
        if (hit == _hovered)
        {
            return;
        }

        _hovered = hit;

        Hovered?.Invoke(this, hit switch
        {
            { IsAggregate: true } aggregate => (null, aggregate.Bytes),
            { } node => (node.Node, null),
            _ => (null, null),
        });
    }

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

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hovered = null;
        Hovered?.Invoke(this, (null, null));
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e) => Pick(e.GetPosition(this));

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(this);

        Pick(point);
        MenuRequested?.Invoke(this, point);
    }

    /// <summary>
    /// Say what is at <paramref name="point"/>. The block standing in for items too small to draw
    /// picks nothing: it is several thousand files at once, and §7.1 has no bulk action.
    /// </summary>
    private void Pick(Point point)
    {
        if (_drawing is not { } drawing)
        {
            return;
        }

        Picked?.Invoke(this, At(drawing, point) switch
        {
            { IsAggregate: false } hit => hit.Node,
            _ => null,
        });
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_drawing is not { } drawing)
        {
            return;
        }

        if (At(drawing, e.GetPosition(this)) is { IsAggregate: false } hit)
        {
            Activated?.Invoke(this, hit.Node);
        }
    }
}
