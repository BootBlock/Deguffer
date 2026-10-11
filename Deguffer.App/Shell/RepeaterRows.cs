using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Deguffer.App.Shell;

/// <summary>
/// A repeater's rows: one element per realised row, made from the repeater's template and recycled
/// as the scroller it sits in moves. A repeater has no scroller of its own, so its rows are seen
/// through the nearest one around it, which may be the page's.
/// </summary>
internal sealed class RepeaterRows : IListRows
{
    private readonly ItemsRepeater _repeater;

    /// <summary>The items view heard for changes, replaced with the repeater's items.</summary>
    private ItemsSourceView? _items;

    public RepeaterRows(ItemsRepeater repeater)
    {
        _repeater = repeater;

        repeater.ElementPrepared += (_, args) =>
        {
            if (repeater.ItemsSourceView?.GetAt(args.Index) is { } row)
            {
                Shown?.Invoke(args.Element, row);
            }
        };
        repeater.ElementClearing += (_, args) => Released?.Invoke(args.Element);
        repeater.RegisterPropertyChangedCallback(ItemsRepeater.ItemsSourceProperty, (_, _) =>
        {
            Hear();

            // New items altogether, which a list view reports as a reset.
            Changed?.Invoke([], true);
        });

        Hear();
    }

    public event Action<UIElement, object>? Shown;

    public event Action<UIElement>? Released;

    public event Action<IReadOnlyList<object>, bool>? Changed;

    public FrameworkElement List => _repeater;

    public ScrollViewer? Scroller() => VisualTree.Ancestor<ScrollViewer>(_repeater);

    public HashSet<object> Held()
    {
        var count = _items?.Count ?? 0;
        var held = new HashSet<object>(count, ReferenceEqualityComparer.Instance);

        for (var at = 0; at < count; at++)
        {
            if (_items!.GetAt(at) is { } row)
            {
                held.Add(row);
            }
        }

        return held;
    }

    public IEnumerable<(UIElement Container, object Row)> Realised()
    {
        if (_items is not { } items)
        {
            yield break;
        }

        // A recycled element stays among the repeater's children, out of sight, and has no index.
        for (var at = 0; at < VisualTreeHelper.GetChildrenCount(_repeater); at++)
        {
            if (VisualTreeHelper.GetChild(_repeater, at) is UIElement element
                && _repeater.GetElementIndex(element) is var index and >= 0
                && index < items.Count
                && items.GetAt(index) is { } row)
            {
                yield return (element, row);
            }
        }
    }

    /// <summary>A repeater's element is its template's root, so the ghost stands where the element did.</summary>
    public RowGhosts.Stood Draw(UIElement container, Rect at, UIElement viewport) =>
        new(at, null, _repeater.ItemTemplate as DataTemplate, _repeater.ItemTemplate as DataTemplateSelector);

    private void Hear()
    {
        if (ReferenceEquals(_items, _repeater.ItemsSourceView))
        {
            return;
        }

        if (_items is not null)
        {
            _items.CollectionChanged -= OnItemsChanged;
        }

        _items = _repeater.ItemsSourceView;

        if (_items is not null)
        {
            _items.CollectionChanged += OnItemsChanged;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        IReadOnlyList<object> arrived = e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { } added
            ? [.. added.Cast<object>()]
            : [];

        Changed?.Invoke(arrived, e.Action == NotifyCollectionChangedAction.Reset);
    }
}
