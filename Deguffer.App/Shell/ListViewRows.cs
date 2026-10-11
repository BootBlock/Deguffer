using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace Deguffer.App.Shell;

/// <summary>
/// A list view's rows: one container per realised row, recycled as the list scrolls.
///
/// <para>Hears the list's own items, which reports a sort's move as a reset. That costs nothing:
/// what a change did to each row is read from where the rows stood and stand, not from what kind of
/// change the list reported.</para>
/// </summary>
internal sealed class ListViewRows : IListRows
{
    private readonly ListViewBase _list;

    public ListViewRows(ListViewBase list)
    {
        _list = list;

        // The framework's own add, delete and reorder transitions would play under the list
        // animation's, and they follow only the system setting, never MotionToken.List.
        list.ItemContainerTransitions = new TransitionCollection();
        list.ContainerContentChanging += OnContainerContentChanging;
        list.Items.VectorChanged += OnItemsChanged;
    }

    public event Action<UIElement, object>? Shown;

    public event Action<UIElement>? Released;

    public event Action<IReadOnlyList<object>, bool>? Changed;

    public FrameworkElement List => _list;

    public ScrollViewer? Scroller() => VisualTree.Descendants<ScrollViewer>(_list).FirstOrDefault();

    public HashSet<object> Held()
    {
        var held = new HashSet<object>(_list.Items.Count, ReferenceEqualityComparer.Instance);

        foreach (var item in _list.Items)
        {
            if (Row(item) is { } row)
            {
                held.Add(row);
            }
        }

        return held;
    }

    public IEnumerable<(UIElement Container, object Row)> Realised()
    {
        if (_list.ItemsPanelRoot is not { } panel)
        {
            yield break;
        }

        foreach (var child in panel.Children)
        {
            if (child is SelectorItem container && Row(_list.ItemFromContainer(container)) is { } row)
            {
                yield return (container, row);
            }
        }
    }

    public RowGhosts.Stood Draw(UIElement container, Rect at, UIElement viewport) =>
        RowGhosts.Stood.Of(_list, (SelectorItem)container, at, viewport);

    /// <summary>
    /// What a row is about. A tree view's flattened list can hold a node for each row rather than the
    /// row itself, and its row is then the node's content.
    /// </summary>
    private static object? Row(object? item) => item is TreeViewNode node ? node.Content : item;

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not { } container)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            Released?.Invoke(container);
        }
        else if (Row(args.Item) is { } row)
        {
            Shown?.Invoke(container, row);
        }
    }

    private void OnItemsChanged(IObservableVector<object> sender, IVectorChangedEventArgs e)
    {
        IReadOnlyList<object> arrived =
            e.CollectionChange == CollectionChange.ItemInserted && e.Index < sender.Count && Row(sender[(int)e.Index]) is { } row
                ? [row]
                : [];

        Changed?.Invoke(arrived, e.CollectionChange == CollectionChange.Reset);
    }
}
