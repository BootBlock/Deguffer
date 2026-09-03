using System.Runtime.InteropServices.WindowsRuntime;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;
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

        SizeChanged += (_, _) => Redraw();
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        DoubleTapped += OnDoubleTapped;

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
        };

        Unloaded += (_, _) =>
        {
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
        AutomationProperties.SetName(this, "Map of the scanned drive");

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

    private void Redraw()
    {
        _labels.Children.Clear();
        _hovered = null;

        _scale = XamlRoot?.RasterizationScale ?? 1;

        var width = (int)Math.Round(ActualWidth * _scale);
        var height = (int)Math.Round(ActualHeight * _scale);

        if (_tree is not { } tree || width <= 0 || height <= 0 || tree.SizeOf(_node) <= 0)
        {
            // The bitmap goes with the source. Keeping it would let the reuse below match on size
            // and never reattach it, leaving the control blank until the window is resized.
            _surface.Source = null;
            _bitmap = null;
            _pixels = null;
            _drawing = null;
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
    /// </summary>
    private void DrawLabels(ExploreTree tree, ExploreSurface drawing)
    {
        foreach (var label in drawing.Labels)
        {
            var text = new TextBlock
            {
                Text = $"{tree.NameOf(label.Node)}  {FreeSpace.Format(tree.SizeOf(label.Node))}",
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = label.Centred ? TextAlignment.Center : TextAlignment.Left,
                Width = label.Width / _scale,
                Foreground = new SolidColorBrush(Color.FromArgb(
                    255, label.Colour.Red, label.Colour.Green, label.Colour.Blue)),
            };

            if (label.Rotation != 0)
            {
                text.RenderTransformOrigin = new Point(0.5, 0.5);
                text.RenderTransform = new RotateTransform { Angle = label.Rotation };
            }

            // Announced through the list view instead: a label here duplicates a row there, and a
            // screen reader reading fifty fragments of a picture helps nobody.
            AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);

            Canvas.SetLeft(text, label.X / _scale);
            Canvas.SetTop(text, label.Y / _scale);

            _labels.Children.Add(text);
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drawing is not { } drawing)
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Position;
        var hit = drawing.At((float)(point.X * _scale), (float)(point.Y * _scale));

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

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_drawing is not { } drawing)
        {
            return;
        }

        var point = e.GetPosition(this);

        if (drawing.At((float)(point.X * _scale), (float)(point.Y * _scale))
            is { IsAggregate: false } hit)
        {
            Activated?.Invoke(this, hit.Node);
        }
    }
}
