using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Memory;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The Memory page: where physical memory is, drawn by the same layouts that draw a scanned drive.
///
/// <para>It answers "where is the memory going", which §7.2 makes a question of its own: Storage
/// answers what is safe to remove, Explore where the space went. This page shows and explains, and
/// acts on nothing at all. There is no button here that closes, stops or empties anything, and there
/// is nothing to select.</para>
///
/// <para>It reads the machine while it is on screen and stops when the reader leaves it, because a
/// page nobody is looking at has no reason to ask Windows anything.</para>
/// </summary>
public sealed partial class MemoryPage : Page
{
    private CancellationTokenSource? _watching;

    public MemoryPage()
    {
        // Assigned before InitializeComponent so no x:Bind can evaluate against a null view-model,
        // which is the order ExplorePage settled on for the same reason.
        ViewModel = new MemoryViewModel(new MemoryFeed(MemorySource.Default, TimeProvider.System));

        ViewModel.ViewChanged += (_, _) => ShowCurrentNode();

        InitializeComponent();

        Map.Hovered += (_, what) => ViewModel.Hover(what.Node, what.AggregateBytes);
        Map.Activated += (_, node) => ViewModel.Descend(node);

        ViewSelector.SelectedIndex = (int)ViewModel.SelectedView;

        Loaded += (_, _) => Watch();
        Unloaded += (_, _) => StopWatching();
    }

    public MemoryViewModel ViewModel { get; }

    /// <summary>
    /// Start reading. Loaded rather than the constructor, because a page is built before it is on
    /// screen, and the first read is what puts something there.
    /// </summary>
    private void Watch()
    {
        StopWatching();

        _watching = new CancellationTokenSource();

        // Not awaited: this is a loop that runs for as long as the page is on screen, and it takes
        // its own failures (see MemoryViewModel.WatchAsync).
        _ = ViewModel.WatchAsync(_watching.Token);
    }

    private void StopWatching()
    {
        _watching?.Cancel();
        _watching?.Dispose();
        _watching = null;
    }

    private void OnViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var view = (ExploreView)ViewSelector.SelectedIndex;

        ViewModel.SelectedView = view;

        var listed = view == ExploreView.List;

        // The list and the picture are the same contents, so exactly one is on screen. The map is
        // hidden rather than told to stop, and it draws nothing while it is hidden.
        RowsList.Visibility = listed ? Visibility.Visible : Visibility.Collapsed;
        Map.Visibility = listed ? Visibility.Collapsed : Visibility.Visible;

        ShowCurrentNode();
    }

    /// <summary>
    /// Draw the current node.
    ///
    /// <para>The colours are asked for per repaint rather than handed over once, which is what the
    /// map's clock reading is for elsewhere. Here they never change: a memory tree has no dates, so
    /// its shapes say which part they belong to.</para>
    /// </summary>
    private void ShowCurrentNode() =>
        Map.Show(ViewModel.Tree, ViewModel.CurrentNode, ViewModel.SelectedView, _ => ShapeColours.ByBranch, ViewModel.LabelFor);

    private void OnAscend(object sender, RoutedEventArgs e) => ViewModel.Ascend();

    private void OnCrumbClicked(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { Tag: int node })
        {
            ViewModel.GoTo(node);
        }
    }

    /// <summary>
    /// Open what a row holds. A click rather than a double click, because nothing here is selected by
    /// clicking: the list offers no action, so a click has only one meaning.
    /// </summary>
    private void OnRowClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MemoryRow row)
        {
            ViewModel.Descend(row.Node);
        }
    }
}
