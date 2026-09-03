using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Deguffer.App.Views;

/// <summary>
/// The Explore page: pick a drive, see what is on it.
///
/// <para>Nothing on this page removes anything. It answers "what is using the space", which §3 is
/// careful to say is not the same question as "what is safe to remove" — the Storage page answers
/// that one, and it answers it with a provider's knowledge of what a directory actually is rather
/// than with its size.</para>
/// </summary>
public sealed partial class ExplorePage : Page
{
    public ExplorePage()
    {
        // Assigned before InitializeComponent so no x:Bind can evaluate against a null view-model,
        // whatever the framework's initialisation order does next.
        ViewModel = new ExploreViewModel(ExploreScanner.Default, VolumeInventory.Current);
        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();
        ViewModel.ViewChanged += (_, _) => ShowCurrentNode();

        InitializeComponent();

        Map.Hovered += (_, what) => ViewModel.Hover(what.Node, what.AggregateBytes);
        Map.Activated += (_, node) => ViewModel.Descend(node);

        // Read once, here, and never again — the same rule the Storage page's density selector
        // follows, and for the same reason: re-reading on every navigation undoes a choice whose
        // write to disk failed, silently and in the same session.
        ShowAs(App.Preferences.Current.Explore);

        // A scan of a full drive takes long enough that throwing it away on a trip to Settings and
        // back would be its own defect.
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    public ExploreViewModel ViewModel { get; }

    /// <summary>
    /// Draw the page in <paramref name="view"/>, and leave the selector agreeing with what is on
    /// screen.
    ///
    /// Safe to call from the selector's own change handler: the first call moves the index off -1
    /// and re-enters that handler once, which lands back here with the index it now holds, and
    /// assigning an unchanged index raises nothing further.
    /// </summary>
    private void ShowAs(ExploreView view)
    {
        ViewModel.SelectedView = view;
        ViewSelector.SelectedIndex = (int)view;

        var listed = view == ExploreView.List;

        RowsList.Visibility = listed ? Visibility.Visible : Visibility.Collapsed;
        Map.Visibility = listed ? Visibility.Collapsed : Visibility.Visible;

        ShowCurrentNode();
    }

    private void ShowCurrentNode() =>
        Map.Show(ViewModel.Tree, ViewModel.CurrentNode, ViewModel.SelectedView);

    /// <summary>
    /// The view is applied first and persisted second, so it takes effect whether or not the
    /// preferences file can be written — <see cref="Shell.PreferenceService"/>'s usual order is
    /// inverted here on the same reasoning the Storage page gives: that order exists so a rejected
    /// write cannot take effect for a setting governing what gets deleted, and this one governs
    /// which picture is drawn.
    /// </summary>
    private void OnViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var view = (ExploreView)ViewSelector.SelectedIndex;

        ShowAs(view);
        App.Preferences.Update(current => current with { Explore = view });
    }

    /// <summary>
    /// Scope the next scan to a folder, through the system picker.
    ///
    /// <para>The picking lives here rather than in the view model for the reason
    /// <see cref="SettingsPage"/> gives: it is a WinUI dialog needing a window handle, and the view
    /// model stays testable by knowing only about the path that comes back. That split is also what
    /// covers this code — the picker opens in a separate broker process and cannot be driven by the
    /// automation harness, while the scan the path produces is exercised in Core.</para>
    /// </summary>
    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        // A picker with no owner throws in a WinUI 3 desktop app rather than opening unowned.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            ViewModel.ScopeTo(folder.Path);
        }
    }

    private void OnCrumbClicked(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { Tag: int node })
        {
            ViewModel.GoTo(node);
        }
    }

    private void OnRowClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ExploreRow row)
        {
            ViewModel.Descend(row.Node);
        }
    }
}
