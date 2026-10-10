using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace Deguffer.App.Shell;

/// <summary>
/// One motion for every bound list: a row arriving fades and slides in, a row leaving fades out
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
/// <para>Hears the list's own items, which reports a sort's move as a reset. That costs nothing:
/// what a change did to each row is read from where the rows stood and stand, not from what kind of
/// change the list reported.</para>
/// </summary>
internal sealed class ListAnimation
{
    private static readonly IEqualityComparer<object> Rows = ReferenceEqualityComparer.Instance;

    private readonly ListViewBase _list;

    private readonly IMotionPolicy _motion;

    /// <summary>The item each container last showed, kept past its recycling until the frame's change is played.</summary>
    private readonly Dictionary<SelectorItem, object> _shown = [];

    /// <summary>The containers recycled since the last change was played, whose items go with it.</summary>
    private readonly HashSet<SelectorItem> _released = [];

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

    private ListAnimation(ListViewBase list, IMotionPolicy motion)
    {
        _list = list;
        _motion = motion;

        // The framework's own add, delete and reorder transitions would play under these, and they
        // follow only the system setting, never MotionToken.List.
        list.ItemContainerTransitions = new TransitionCollection();
        list.ContainerContentChanging += OnContainerContentChanging;
        list.Items.VectorChanged += OnItemsChanged;
        list.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        list.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => _pointer = null), handledEventsToo: true);
        list.Loaded += (_, _) => FindViewport();
    }

    /// <summary>
    /// Raised for each row as its arrival plays, with the container it arrives in, so what the row
    /// draws can come in with it. Never for a row only scrolled into sight, which has not arrived.
    /// </summary>
    public event Action<SelectorItem>? Arrived;

    /// <summary>Give <paramref name="list"/> the motion, for as long as it lives.</summary>
    public static ListAnimation Play(ListViewBase list, IMotionPolicy motion) => new(list, motion);

    /// <summary>
    /// Give the list inside <paramref name="tree"/> the motion. A tree view lays its rows out as a
    /// flattened list in its template, which is applied when the tree is first laid out, and not
    /// before for a tree that loads collapsed, so the list is looked for until it is there.
    /// </summary>
    public static void Play(TreeView tree, IMotionPolicy motion)
    {
        tree.LayoutUpdated += Find;

        void Find(object? sender, object e)
        {
            if (Descendant<TreeViewList>(tree) is { } list)
            {
                tree.LayoutUpdated -= Find;
                new ListAnimation(list, motion).FindViewport();
            }
        }
    }

    private void FindViewport()
    {
        if (_viewport is not null)
        {
            return;
        }

        _scroller = Descendant<ScrollViewer>(_list);
        _viewport = _scroller is null ? null : Descendant<ScrollContentPresenter>(_scroller);
        _ghosts = _viewport is null ? null : RowGhosts.Over(_viewport);
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not { } container)
        {
            return;
        }

        // A container shown again, for this item or another, starts at rest: a fade or a glide left
        // on it from its last row would play on a row that never moved.
        _moves.Rest(container);

        if (args.InRecycleQueue)
        {
            _released.Add(container);
        }
        else if (Row(args.Item) is { } row)
        {
            _released.Remove(container);
            _shown[container] = row;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) =>
        _pointer = _viewport is null ? null : e.GetCurrentPoint(_viewport).Position;

    private void OnItemsChanged(IObservableVector<object> sender, IVectorChangedEventArgs e)
    {
        // A list that was collapsed when it loaded had no template to look in until it was shown.
        FindViewport();

        if (_viewport is null || !_list.IsLoaded)
        {
            return;
        }

        if (!_pending)
        {
            _pending = true;
            Stand();
            CompositionTarget.Rendering += OnRendering;
        }

        _reset |= e.CollectionChange == CollectionChange.Reset;

        if (e.CollectionChange == CollectionChange.ItemInserted && e.Index < sender.Count && Row(sender[(int)e.Index]) is { } arriving)
        {
            _arrived.Add(arriving);
        }
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
                _drawn.TryAdd(item, RowGhosts.Stood.Of(_list, container, at, _viewport));
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

        _list.UpdateLayout();

        var before = _stood.ToDictionary(row => row.Key, row => row.Value.Y, Rows);
        var after = Standing();

        if (Anchors() is { Count: > 0 } anchors
            && ListMotion.Hold(anchors, before, after.Select(row => (row.Item, row.At.Y)).ToList(), Rows) is { } shift
            && _scroller is not null)
        {
            _scroller.ChangeView(null, _scroller.VerticalOffset + shift, null, disableAnimation: true);
            _list.UpdateLayout();
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
            if (!after.Exists(row => ReferenceEquals(row.Item, item)) && !(held ??= Held()).Contains(item))
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

    /// <summary>
    /// What a row is about. A tree view's flattened list can hold a node for each row rather than the
    /// row itself, and its row is then the node's content.
    /// </summary>
    private static object? Row(object? item) => item is TreeViewNode node ? node.Content : item;

    /// <summary>Every row the list holds, on screen or not.</summary>
    private HashSet<object> Held()
    {
        var held = new HashSet<object>(_list.Items.Count, Rows);

        foreach (var item in _list.Items)
        {
            if (Row(item) is { } row)
            {
                held.Add(row);
            }
        }

        return held;
    }
    /// <summary>Every row on screen now, and where it stands, from the top down.</summary>
    private List<(object Item, SelectorItem Container, Rect At)> Standing()
    {
        var rows = new List<(object, SelectorItem, Rect)>();

        if (_list.ItemsPanelRoot is not { } panel)
        {
            return rows;
        }

        var height = _viewport!.ActualHeight;

        foreach (var child in panel.Children)
        {
            if (child is SelectorItem container
                && !_released.Contains(container)
                && Row(_list.ItemFromContainer(container)) is { } item
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

        if (FocusManager.GetFocusedElement(_list.XamlRoot) is SelectorItem focused
            && Row(_list.ItemFromContainer(focused)) is { } focusedItem)
        {
            anchors.Add(focusedItem);
        }

        return anchors;
    }

    /// <summary>
    /// Where <paramref name="container"/> is laid out in the viewport, or null for one the list has
    /// taken out of its tree, which has no place to measure from.
    /// </summary>
    private Rect? Place(SelectorItem container)
    {
        if (VisualTreeHelper.GetParent(container) is null)
        {
            return null;
        }

        var origin = container.TransformToVisual(_viewport).TransformPoint(default);

        return new Rect(origin.X, origin.Y, container.ActualWidth, container.ActualHeight);
    }

    private static SelectorItem? Container(List<(object Item, SelectorItem Container, Rect At)> rows, object item) =>
        rows.Find(row => ReferenceEquals(row.Item, item)).Container;

    private static T? Descendant<T>(DependencyObject root)
        where T : DependencyObject => VisualTree.Descendants<T>(root).FirstOrDefault();
}
