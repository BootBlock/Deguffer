using System.ComponentModel;
using Deguffer.App.Controls;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Deguffer.App.Views;

/// <summary>
/// The Explore page's two cards and its map, joined (#286): a row of <b>What grew</b> or <b>What kind
/// of files</b> pointed at or focused lights its shapes on the map and dims the rest, a shape pointed
/// at on the map marks its row in the card that is open, and the cards' bars grow to their figures as
/// a card opens or its figures change.
///
/// <para>The card answers "what" and the map "where", so each answers the other's question without
/// the reader changing what the colours say. Which shapes are lit, and which row speaks for a shape,
/// are Core's (<see cref="MapLight"/>, <see cref="Core.Exploring.History.ScanGrowth.ListedFor"/>,
/// <see cref="Core.Exploring.Files.DominantTypes.KindOf"/>). This decides only when to ask.</para>
///
/// <para>§7.1: nothing here picks anything. A light and a marked row say where and what, and the
/// selection a Delete acts on is the view model's and untouched.</para>
/// </summary>
internal sealed class ExploreCards
{
    private readonly ExploreViewModel _page;

    private readonly ExploreMap _map;

    private readonly ListViewBase _growthRows;

    private readonly ListViewBase _typeRows;

    private readonly ItemsControl _usedSpace;

    /// <summary>The card row under the pointer, which lights the map ahead of the one with focus.</summary>
    private object? _pointed;

    /// <summary>The card row with keyboard focus, which lights the map as a pointer over it does.</summary>
    private object? _focused;

    /// <summary>Whether the used-space bars are already waiting to grow in.</summary>
    private bool _usedSpaceOwed;

    public ExploreCards(ExploreViewModel page, ExploreMap map, ListViewBase growthRows, ListViewBase typeRows, ItemsControl usedSpace)
    {
        _page = page;
        _map = map;
        _growthRows = growthRows;
        _typeRows = typeRows;
        _usedSpace = usedSpace;

        ListAnimation.Play(growthRows, SystemMotion.Current);

        // A kind's bar comes in from nothing as its row arrives, rather than standing at its figure
        // under a row still fading in. A row only scrolled into sight has not arrived.
        ListAnimation.Play(typeRows, SystemMotion.Current).Arrived += container => Grow(container);

        foreach (var rows in new[] { growthRows, typeRows })
        {
            rows.ContainerContentChanging += (_, args) => Mark(args.ItemContainer, IsPointedAt(args.Item));
            rows.GotFocus += (_, e) => Focus((e.OriginalSource as SelectorItem)?.Content);
            rows.LostFocus += (_, _) => Focus(null);
        }

        // Laid out after the change that asks, so the bars that came with it are there to grow, and
        // once for a rewrite that changes several.
        page.Growth.UsedSpace.CollectionChanged += (_, _) =>
        {
            if (!_usedSpaceOwed)
            {
                _usedSpaceOwed = true;
                Later(() =>
                {
                    _usedSpaceOwed = false;
                    Grow(usedSpace);
                });
            }
        };

        page.Growth.PropertyChanged += OnCardChanged;
        page.Types.PropertyChanged += OnCardChanged;
        page.Growth.Changed += (_, _) => Relight();
        page.Types.Changed += (_, _) => Relight();
        page.PropertyChanged += OnPageChanged;
    }

    /// <summary>The pointer came over a row of either card.</summary>
    public void Entered(object? row)
    {
        _pointed = row;
        Relight();
    }

    /// <summary>The pointer left a row of either card.</summary>
    public void Exited(object? row)
    {
        if (ReferenceEquals(row, _pointed))
        {
            _pointed = null;
        }

        Relight();
    }

    /// <summary>
    /// Light again what the rows pointed at name, in the tree on screen now: the page calls it with
    /// every map it shows, because a light names nodes of one tree and lights nothing in another.
    /// </summary>
    public void Relight() =>
        _map.Light((_pointed ?? _focused) switch
        {
            ExploreTypeRow kind when _page.ShowsTypes && _page.Tree is { } tree && _page.Types.Rows.Contains(kind) =>
                MapLight.Kind(tree, _page.Types.Dominant, kind.Category),

            // Only a row still in the list: one carried over from an earlier comparison names a node of
            // that comparison's tree.
            ExploreGrowthRow { CanOpen: true } folder when _page.ShowsGrowth && _page.Growth.Comparison is { } growth
                && _page.Growth.Rows.Contains(folder) =>
                MapLight.Folder(growth.Tree, folder.Node),

            _ => null,
        });

    private void Focus(object? row)
    {
        _focused = row;
        Relight();
    }

    /// <summary>Mark the row the map is pointing at, in whichever card asked.</summary>
    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExploreTypes.Pointed) or nameof(ExploreGrowth.Pointed))
        {
            MarkAll(ReferenceEquals(sender, _page.Types) ? _typeRows : _growthRows);
        }
    }

    /// <summary>A card opened or closed: its bars grow in, and a closed card lights nothing.</summary>
    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExploreViewModel.ShowsTypes))
        {
            if (_page.ShowsTypes)
            {
                Later(() => Grow(_typeRows));
            }

            Relight();
        }
        else if (e.PropertyName == nameof(ExploreViewModel.ShowsGrowth))
        {
            if (_page.ShowsGrowth)
            {
                Later(() => Grow(_usedSpace));
            }

            Relight();
        }
    }

    private bool IsPointedAt(object? row) => row switch
    {
        ExploreTypeRow kind => kind.Category == _page.Types.Pointed,
        ExploreGrowthRow folder => folder.Path == _page.Growth.Pointed,
        _ => false,
    };

    private void MarkAll(ListViewBase rows)
    {
        for (var i = 0; i < rows.Items.Count; i++)
        {
            if (rows.ContainerFromIndex(i) is SelectorItem container)
            {
                Mark(container, IsPointedAt(rows.Items[i]));
            }
        }
    }

    /// <summary>
    /// Show or hide a row's mark, which its template draws under its text in the theme's own fill.
    /// Set on the container rather than bound, because the row does not know what the map points at,
    /// and a list hands its containers from row to row as it scrolls, which is when this is asked again.
    /// </summary>
    private static void Mark(SelectorItem container, bool marked)
    {
        if (container.ContentTemplateRoot is FrameworkElement root && root.FindName("Mark") is UIElement mark)
        {
            mark.Opacity = marked ? 1 : 0;
        }
    }

    /// <summary>Grow every bar under <paramref name="holder"/> in from nothing.</summary>
    private static void Grow(DependencyObject holder)
    {
        foreach (var bar in VisualTree.Descendants<FillBar>(holder))
        {
            bar.Grow();
        }

        foreach (var column in VisualTree.Descendants<ColumnBar>(holder))
        {
            column.Grow();
        }
    }

    /// <summary>Run <paramref name="action"/> once the change being made now has been laid out.</summary>
    private void Later(Action action) => _map.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => action());
}
