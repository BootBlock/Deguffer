using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Deguffer.App.Shell;

/// <summary>
/// One motion for every bound list, whether a list view, a tree view or a repeater lays its rows out
/// (<see cref="IListRows"/>): a row arriving fades and slides in, a row leaving fades out
/// where it stood while the rows below glide up into its space, and a row the sort moved glides to
/// its new place. A change that replaces most of what is on screen crossfades instead
/// (<see cref="ListMotion"/>, timed by <see cref="MotionToken.List"/>).
///
/// <para>Played the way a layout animation is: where every row on screen stands is read as the
/// first change of a frame arrives, before the list has laid itself out again, and once the frame's
/// changes are all in, the list is laid out and each row is drawn back where it stood and moved to
/// where it now is, on the compositor. A row only scrolled into or out of sight is not a change, so
/// scrolling, and the containers a long list recycles as it scrolls, never animate.</para>
///
/// <para>Only realised rows are read and moved, so a list of thousands animates its window of rows,
/// and the whole list is read once for a change only when a row has gone from the screen. The row under
/// the pointer, or failing that the one with focus, keeps its place on screen unless it is the one
/// that changed (<see cref="ListMotion.Hold"/>).</para>
///
/// <para>What a change did to each row is read from where the rows stood and stand, not from what
/// kind of change the list reported, so a list that reports a sort's move as a reset costs nothing.</para>
/// </summary>
internal sealed class ListAnimation
{
    private static readonly IEqualityComparer<object> Rows = ReferenceEqualityComparer.Instance;

    private readonly IListRows _rows;

    private readonly IMotionPolicy _motion;

    /// <summary>The item each container last showed, kept past its recycling until the frame's change is played.</summary>
    private readonly Dictionary<UIElement, object> _shown = [];

    /// <summary>The containers recycled since the last change was played, whose items go with it.</summary>
    private readonly HashSet<UIElement> _released = [];

    private readonly RowMoves _moves = new();

    /// <summary>Where each row on screen stood, as drawn, when the frame's first change arrived.</summary>
    private readonly Dictionary<object, Rect> _stood = new(Rows);

    /// <summary>How each of those rows was drawn, for a ghost of it should it leave.</summary>
    private readonly Dictionary<object, RowGhosts.Stood> _drawn = new(Rows);

    /// <summary>The items the frame's changes put into the list.</summary>
    private readonly HashSet<object> _arrived = new(Rows);

    private ScrollViewer? _scroller;

    private ScrollContentPresenter? _viewport;

    private RowGhosts? _ghosts;

    /// <summary>Where the pointer is over the viewport, or null while it is elsewhere.</summary>
    private Point? _pointer;

    private bool _pending;

    /// <summary>
    /// Whether the frame's changes included a reset, after which nothing says which rows are new: a
    /// row on screen that was not on screen before is then taken to have arrived.
    /// </summary>
    private bool _reset;

    private ListAnimation(IListRows rows, IMotionPolicy motion)
    {
        _rows = rows;
        _motion = motion;

        rows.Shown += OnShown;
        rows.Released += OnReleased;
        rows.Changed += OnItemsChanged;

        var list = rows.List;
        list.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        list.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => _pointer = null), handledEventsToo: true);
        list.Loaded += (_, _) => FindViewport();
    }

    /// <summary>
    /// Raised for each row as its arrival plays, with the container it arrives in, so what the row
    /// draws can come in with it. Never for a row only scrolled into sight, which has not arrived.
    /// </summary>
    public event Action<UIElement>? Arrived;

    /// <summary>Give <paramref name="list"/> the motion, for as long as it lives.</summary>
    public static ListAnimation Play(ListViewBase list, IMotionPolicy motion) => new(new ListViewRows(list), motion);

    /// <summary>
    /// Give <paramref name="repeater"/> the motion, for as long as it lives. Its rows are seen through
    /// the scroller around it, and only those inside that scroller's viewport move.
    /// </summary>
    public static ListAnimation Play(ItemsRepeater repeater, IMotionPolicy motion) => new(new RepeaterRows(repeater), motion);

    /// <summary>
    /// Give the list inside <paramref name="tree"/> the motion. A tree view lays its rows out as a
    /// flattened list in its template, which is applied when the tree is first laid out, and not
    /// before for a tree that loads collapsed, so the list is looked for until it is there. The
    /// animation is made only then, so a row's arrival is heard through <paramref name="arrived"/>
    /// rather than through <see cref="Arrived"/>.
    /// </summary>
    public static void Play(TreeView tree, IMotionPolicy motion, Action<UIElement>? arrived = null)
    {
        tree.LayoutUpdated += Find;

        void Find(object? sender, object e)
        {
            if (Descendant<TreeViewList>(tree) is { } list)
            {
                tree.LayoutUpdated -= Find;
                var animation = new ListAnimation(new ListViewRows(list), motion);
                animation.Arrived += arrived;
                animation.FindViewport();
            }
        }
    }

    private void FindViewport()
    {
        if (_viewport is not null)
        {
            return;
        }

        _scroller = _rows.Scroller();
        _viewport = _scroller is null ? null : Descendant<ScrollContentPresenter>(_scroller);
        _ghosts = _viewport is null ? null : RowGhosts.Over(_viewport);
    }

    // A container shown again, for this item or another, starts at rest: a fade or a glide left on
    // it from its last row would play on a row that never moved.
    private void OnShown(UIElement container, object row)
    {
        _moves.Rest(container);
        _released.Remove(container);
        _shown[container] = row;
    }

    private void OnReleased(UIElement container)
    {
        _moves.Rest(container);
        _released.Add(container);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) =>
        _pointer = _viewport is null ? null : e.GetCurrentPoint(_viewport).Position;

    private void OnItemsChanged(IReadOnlyList<object> arrived, bool reset)
    {
        // A list that was collapsed when it loaded had no template to look in until it was shown.
        FindViewport();

        if (_viewport is null || !_rows.List.IsLoaded)
        {
            return;
        }

        if (!_pending)
        {
            _pending = true;
            Stand();
            CompositionTarget.Rendering += OnRendering;
        }

        _reset |= reset;
        _arrived.UnionWith(arrived);
    }

    /// <summary>
    /// Read where every row on screen stands, as drawn: a row part of the way through a glide is
    /// where the glide has it, so a change during one carries on from there rather than jumping.
    /// </summary>
    private void Stand()
    {
        var height = _viewport!.ActualHeight;

        foreach (var (container, item) in _shown)
        {
            if (Place(container) is not { } at)
            {
                continue;
            }

            at.Y += _moves.Offset(container);

            if (at.Bottom > 0 && at.Top < height)
            {
                _stood.TryAdd(item, at);
                _drawn.TryAdd(item, _rows.Draw(container, at, _viewport));
            }
        }
    }

    private void OnRendering(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _pending = false;

        try
        {
            Settle();
        }
        finally
        {
            _stood.Clear();
            _drawn.Clear();
            _arrived.Clear();
            _reset = false;

            foreach (var container in _released)
            {
                _shown.Remove(container);
            }

            _released.Clear();
        }
    }

    /// <summary>Lay the list out as the frame's changes left it, and play what each row on screen does.</summary>
    private void Settle()
    {
        var played = _motion.For(MotionToken.List);

        _rows.List.UpdateLayout();

        var before = _stood.ToDictionary(row => row.Key, row => row.Value.Y, Rows);
        var after = Standing();

        if (Anchors() is { Count: > 0 } anchors
            && ListMotion.Hold(anchors, before, after.Select(row => (row.Item, row.At.Y)).ToList(), Rows) is { } shift
            && _scroller is not null)
        {
            _scroller.ChangeView(null, _scroller.VerticalOffset + shift, null, disableAnimation: true);
            _rows.List.UpdateLayout();
            after = Standing();
        }

        if (_reset)
        {
            foreach (var row in after)
            {
                if (!before.ContainsKey(row.Item))
                {
                    _arrived.Add(row.Item);
                }
            }
        }

        // A row on screen before and not now has either left the list or only moved out of sight.
        // Telling the two apart takes the list's whole contents, read once, and only when there is
        // such a row.
        var departed = new HashSet<object>(Rows);
        HashSet<object>? held = null;

        foreach (var item in before.Keys)
        {
            if (!after.Exists(row => ReferenceEquals(row.Item, item)) && !(held ??= _rows.Held()).Contains(item))
            {
                departed.Add(item);
            }
        }
        var plan = ListMotion.Plan(
            before,
            after.Select(row => (row.Item, row.At.Y)).ToList(),
            _arrived,
            departed,
            played,
            Rows);

        foreach (var row in plan.Rows)
        {
            switch (row.Move)
            {
                case RowMove.Leave when _drawn.TryGetValue(row.Key, out var stood):
                    _ghosts?.Leave(row.Key, stood, played);
                    break;
                case RowMove.Arrive when Container(after, row.Key) is { } arriving:
                    _moves.Arrive(arriving, played, slides: !plan.Crossfade);
                    Arrived?.Invoke(arriving);
                    break;
                case RowMove.Glide when Container(after, row.Key) is { } gliding:
                    _moves.Glide(gliding, (float)row.From, played);
                    break;
            }
        }
    }

    /// <summary>Every row on screen now, and where it stands, from the top down.</summary>
    private List<(object Item, UIElement Container, Rect At)> Standing()
    {
        var rows = new List<(object, UIElement, Rect)>();
        var height = _viewport!.ActualHeight;

        foreach (var (container, item) in _rows.Realised())
        {
            if (!_released.Contains(container)
                && Place(container) is { } at
                && at.Bottom > 0
                && at.Top < height)
            {
                rows.Add((item, container, at));
            }
        }

        rows.Sort((one, other) => one.Item3.Y.CompareTo(other.Item3.Y));

        return rows;
    }

    /// <summary>The rows the reader is at: under the pointer, then with focus.</summary>
    private List<object> Anchors()
    {
        var anchors = new List<object>(2);

        if (_pointer is { } pointer)
        {
            foreach (var (item, stood) in _stood)
            {
                if (stood.Contains(pointer))
                {
                    anchors.Add(item);
                    break;
                }
            }
        }

        if (Focused() is { } focused)
        {
            anchors.Add(focused);
        }

        return anchors;
    }

    /// <summary>
    /// The row that holds the keyboard focus, whether the focus is on its container or on a control
    /// in it, such as a row's button in a list whose rows take no focus of their own.
    /// </summary>
    private object? Focused()
    {
        for (var at = FocusManager.GetFocusedElement(_rows.List.XamlRoot) as DependencyObject;
             at is not null && !ReferenceEquals(at, _rows.List);
             at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement container && !_released.Contains(container) && _shown.TryGetValue(container, out var row))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// Where <paramref name="container"/> is laid out in the viewport, or null for one the list has
    /// taken out of its tree, which has no place to measure from.
    /// </summary>
    private Rect? Place(UIElement container)
    {
        if (VisualTreeHelper.GetParent(container) is null)
        {
            return null;
        }

        var origin = container.TransformToVisual(_viewport).TransformPoint(default);

        return new Rect(origin.X, origin.Y, container.ActualSize.X, container.ActualSize.Y);
    }

    private static UIElement? Container(List<(object Item, UIElement Container, Rect At)> rows, object item) =>
        rows.Find(row => ReferenceEquals(row.Item, item)).Container;

    private static T? Descendant<T>(DependencyObject root)
        where T : DependencyObject => VisualTree.Descendants<T>(root).FirstOrDefault();
}
