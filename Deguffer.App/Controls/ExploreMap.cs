using System.Runtime.InteropServices.WindowsRuntime;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// Draws a scanned tree, and says what the pointer is over.
///
/// <para>The geometry is one bitmap. A full volume lays out to tens of thousands of rectangles, and
/// the framework's own performance guidance is that a vector element repeated enough times should
/// become an image instead — which is also what every reference implementation does, WinDirStat
/// rendering into a cached surface and blitting it rather than keeping a shape per file.</para>
///
/// <para>The labels are not in the bitmap. They are real text controls laid over it, so they scale
/// with the user's text size and a screen reader can read them — none of which text burnt into a
/// bitmap offers. There are only ever a few dozen, because a rectangle too small to read is a
/// rectangle with no label.</para>
///
/// <para>Nothing here knows what a drive is or how one is scanned. It is handed a tree, a node and
/// a view, and it reports back what was pointed at (G1).</para>
/// </summary>
public sealed class ExploreMap : UserControl
{
    /// <summary>
    /// How many labels to draw at most.
    ///
    /// <para>The size threshold already keeps this small on an ordinary tree, but a directory of
    /// several hundred near-equal children defeats it — every rectangle is then big enough to label
    /// and none of them is interesting. Past a few dozen the labels are noise over the picture
    /// anyway, and the list view is the honest way to read that many names.</para>
    /// </summary>
    private const int MaximumLabels = 64;

    private readonly Image _surface = new()
    {
        // The bitmap is rendered at the display's pixel size and stretched back over the control's
        // logical size, so this maps one bitmap pixel to one device pixel rather than resampling.
        Stretch = Stretch.Fill,
    };

    private readonly Canvas _labels = new()
    {
        // Labels sit over the map and are never the thing being clicked: a click that landed on a
        // label rather than the rectangle under it would select whatever the label happened to
        // overlap.
        IsHitTestVisible = false,
    };

    private readonly Dictionary<int, int> _branches = [];

    private ExploreTree? _tree;
    private int _node;
    private ExploreView _view = ExploreView.Treemap;
    private IReadOnlyList<ExploreTile> _tiles = [];
    private TileHitTest? _hits;
    private WriteableBitmap? _bitmap;
    private double _scale = 1;
    private int? _hovered;

    public ExploreMap()
    {
        Content = new Grid { Children = { _surface, _labels } };

        SizeChanged += (_, _) => Redraw();
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        DoubleTapped += OnDoubleTapped;

        // The map is one focusable thing rather than one per rectangle, and the status line beside
        // it carries what is under the pointer. A screen reader needs the same information without
        // a pointer, which the list view provides in full — so this announces its role and defers.
        IsTabStop = true;
        AutomationProperties.SetName(this, "Map of the scanned drive");
    }

    /// <summary>The node the user asked to open, by double-clicking a rectangle.</summary>
    public event EventHandler<int>? Activated;

    /// <summary>
    /// What the pointer moved over: a node, or a byte count where it is over the rectangle standing
    /// in for items too small to draw. Both null when it is over nothing.
    /// </summary>
    public event EventHandler<(int? Node, long? AggregateBytes)>? Hovered;

    /// <summary>Draw <paramref name="node"/> of <paramref name="tree"/> in <paramref name="view"/>.</summary>
    public void Show(ExploreTree? tree, int node, ExploreView view)
    {
        _tree = tree;
        _node = node;
        _view = view;

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
            _tiles = [];
            _hits = null;
            return;
        }

        // The hue each direct child gets, fixed before anything is drawn so the map, the labels and
        // any later selection all agree on it.
        _branches.Clear();

        var children = tree.ChildrenOf(_node);
        for (var i = 0; i < children.Length; i++)
        {
            _branches[children[i]] = i;
        }

        // Every pixel threshold in the layout is in device-independent units, so a 3-pixel floor at
        // 100% must stay 3 logical pixels at 200% rather than becoming a pixel and a half.
        var limits = LayoutLimits.Default with
        {
            MinimumTileSize = (float)(LayoutLimits.Default.MinimumTileSize * _scale),
        };

        _tiles = _view == ExploreView.Icicle
            ? IcicleLayout.Compute(tree, _node, width, height, (float)(22 * _scale), limits)
            : TreemapLayout.Compute(tree, _node, width, height, limits);

        _hits = new TileHitTest(_tiles, width, height);

        var pixels = TileRasteriser.Paint(_tiles, width, height, Ground(), BranchOf);

        // Reused while the size holds. A scan redraws this every three quarters of a second, and a
        // fresh bitmap each time is several megabytes of garbage per second for a surface whose
        // dimensions only change when the window does (G5).
        if (_bitmap is not { } bitmap || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
        {
            bitmap = new WriteableBitmap(width, height);
            _bitmap = bitmap;
            _surface.Source = bitmap;
        }

        pixels.CopyTo(0, bitmap.PixelBuffer, 0, pixels.Length);
        bitmap.Invalidate();

        DrawLabels(tree);
    }

    /// <summary>
    /// The canvas ground, taken from the theme rather than fixed.
    ///
    /// <para>§6.5 requires the UI to read correctly on a flat background in either theme, and this
    /// is where the reference implementation took the shortcut this cannot: WinDirStat's newer views
    /// hard-code a near-black ground and are dark whatever the system is set to.</para>
    /// </summary>
    private TileColour Ground()
    {
        if (Application.Current.Resources["LayerFillColorDefaultBrush"] is SolidColorBrush brush)
        {
            return new TileColour(brush.Color.R, brush.Color.G, brush.Color.B);
        }

        return ActualTheme == ElementTheme.Dark
            ? new TileColour(32, 32, 32)
            : new TileColour(243, 243, 243);
    }

    /// <summary>
    /// Which top-level branch a node belongs to, so a whole subtree shares one hue.
    ///
    /// <para>Walked up from the node rather than carried in the tile, because "top level" means
    /// relative to whatever the user has descended into — the same node is a branch of its own when
    /// opened, and part of a larger one when seen from above.</para>
    ///
    /// <para>The answer is the branch's <em>position</em> among its siblings, not its node number.
    /// Node numbers are whatever the scan happened to assign, so taking them modulo the palette
    /// gives two adjacent branches the same hue often enough to be visible — and the two largest
    /// rectangles on the screen sharing a colour is precisely the collision that matters.</para>
    /// </summary>
    private int BranchOf(int node)
    {
        if (_tree is not { } tree)
        {
            return 0;
        }

        var current = node;

        while (current != _node && tree.ParentOf(current) != _node && current != tree.RootNode)
        {
            current = tree.ParentOf(current);
        }

        return _branches.TryGetValue(current, out var position) ? position : 0;
    }

    private void DrawLabels(ExploreTree tree)
    {
        var drawn = 0;

        foreach (var tile in _tiles)
        {
            if (drawn >= MaximumLabels)
            {
                return;
            }

            if (tile.IsAggregate || !TileRasteriser.CanCarryLabel(tile) || tile.Node == _node)
            {
                continue;
            }

            var colour = TilePalette.For(BranchOf(tile.Node), tile.Depth).ContrastingText;

            var text = new TextBlock
            {
                Text = $"{tree.NameOf(tile.Node)}  {FreeSpace.Format(tree.SizeOf(tile.Node))}",
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = (tile.Width / _scale) - 8,
                Foreground = new SolidColorBrush(Color.FromArgb(255, colour.Red, colour.Green, colour.Blue)),

                // Announced through the list view instead: a label here duplicates a row there, and
                // a screen reader reading fifty overlapping fragments of a picture helps nobody.
                Visibility = Visibility.Visible,
            };

            AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);

            Canvas.SetLeft(text, (tile.X / _scale) + 4);
            Canvas.SetTop(text, (tile.Y / _scale) + 2);

            _labels.Children.Add(text);
            drawn++;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_hits is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Position;
        var index = _hits.At((float)(point.X * _scale), (float)(point.Y * _scale));

        // Only when it changed. A pointer moves at the display's refresh rate and lands in the same
        // rectangle for most of that, so reporting every move would rebuild the same string sixty
        // times a second.
        if (index == _hovered)
        {
            return;
        }

        _hovered = index;

        var tile = index is { } i ? _tiles[i] : (ExploreTile?)null;

        Hovered?.Invoke(this, tile switch
        {
            { IsAggregate: true } aggregate => (null, aggregate.Bytes),
            { } node => (node.Node, null),
            _ => (null, null),
        });
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hovered = null;
        Hovered?.Invoke(this, (null, null));
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_hits is null)
        {
            return;
        }

        var point = e.GetPosition(this);

        if (_hits.At((float)(point.X * _scale), (float)(point.Y * _scale)) is { } index
            && !_tiles[index].IsAggregate)
        {
            Activated?.Invoke(this, _tiles[index].Node);
        }
    }
}
