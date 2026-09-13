using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Memory;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Deguffer.App.Views;

/// <summary>
/// The Memory page: where physical memory is, drawn by the same layouts that draw a scanned drive.
///
/// <para>It answers "where is the memory going", which §7.2 makes a question of its own: Storage
/// answers what is safe to remove, Explore where the space went. This page shows and explains, and
/// acts on nothing at all. There is no button here that closes, stops or empties anything, and there
/// is nothing to select.</para>
///
/// <para>It reads the machine while someone can see it, and stops otherwise, because a page nobody is
/// looking at has no reason to ask Windows anything.</para>
/// </summary>
public sealed partial class MemoryPage : Page
{
    private CancellationTokenSource? _watching;
    private bool _onScreen;

    public MemoryPage()
    {
        // Assigned before InitializeComponent so no x:Bind can evaluate against a null view-model,
        // which is the order ExplorePage settled on for the same reason.
        ViewModel = new MemoryViewModel(new MemoryFeed(MemorySource.Default, TimeProvider.System));

        ViewModel.ViewChanged += (_, _) => ShowCurrentNode();

        InitializeComponent();

        // Required rather than Enabled, as the other destinations are: Enabled is subject to the
        // frame's cache size and Required is not, and a page rebuilt on return loses which picture
        // the reader chose.
        NavigationCacheMode = NavigationCacheMode.Required;

        Map.Hovered += (_, what) => ViewModel.Hover(what.Node, what.AggregateBytes);
        Map.Activated += (_, node) => ViewModel.Descend(node);

        ViewSelector.SelectedIndex = (int)ViewModel.SelectedView;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public MemoryViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _onScreen = true;

        if (XamlRoot is { } root)
        {
            root.Changed += OnRootChanged;
        }

        WatchWhileAnyoneCanSeeThis();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _onScreen = false;

        if (XamlRoot is { } root)
        {
            root.Changed -= OnRootChanged;
        }

        StopWatching();
    }

    /// <summary>
    /// The window was minimised, restored, resized or moved between displays. Only the first two
    /// matter here, and the property answers all four.
    /// </summary>
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        WatchWhileAnyoneCanSeeThis();

    /// <summary>
    /// Read while this page is on screen and the window is not hidden, and not otherwise.
    ///
    /// <para>Minimised, the window keeps its content sized and its elements visible, so nothing else
    /// here would stop it: a Deguffer left minimised on this page would go on reading the machine,
    /// building a tree and shading every pixel of the picture every couple of seconds, for nobody.</para>
    ///
    /// <para>Started and stopped only on a change of answer. A resize raises this event too, and
    /// restarting the loop each time would put the cadence back to nothing every time a window edge
    /// moved.</para>
    /// </summary>
    private void WatchWhileAnyoneCanSeeThis()
    {
        var wanted = _onScreen && XamlRoot is not { IsHostVisible: false };

        if (wanted == (_watching is not null))
        {
            return;
        }

        if (wanted)
        {
            Watch();
        }
        else
        {
            StopWatching();
        }
    }

    private void Watch()
    {
        StopWatching();

        _watching = new CancellationTokenSource();

        // Not awaited: this is a loop that runs for as long as the page is being looked at, and it
        // takes its own failures (see MemoryViewModel.WatchAsync).
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
        if (sender is HyperlinkButton { DataContext: MemoryCrumb crumb })
        {
            ViewModel.GoTo(crumb);
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
