using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Cloud;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Deguffer.App.Views;

/// <summary>
/// The Duplicates destination (§7.4). Wires the view-model to the machine and to the folder picker;
/// every decision is Core's.
/// </summary>
public sealed partial class DuplicatesPage : Page
{
    private bool _shown;

    public DuplicatesPage()
    {
        var finder = new CandidateFinder(
            new ExploreScanner(tuning: App.ScanTuning),
            VolumeInventory.Current,
            WindowsUninstallRegistry.Default,
            UserEnvironment.Current,
            SystemDirectories.Current);

        var run = new DuplicateSearchRun(
            new DuplicateSearcher(finder, App.Media),
            MachineProtections.ForThisMachineAsync,
            UserEnvironment.Current,
            CloudFiles.Default,
            VolumeInventory.Current,
            App.Media);

        // Assigned before InitializeComponent so no x:Bind evaluates against a null view-model.
        ViewModel = new DuplicatesViewModel(
            run.RunAsync,
            App.Preferences,
            new DriveList(VolumeInventory.Current, TimeProvider.System),
            ElevatedRelaunch.IsElevated,
            ElevatedRelaunch.TryRelaunch,
            App.Running,
            ElevatedRelaunch.Requested as DuplicatesRequest);

        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();

        InitializeComponent();

        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += OnLoaded;
    }

    public DuplicatesViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Locations.RefreshDrives();

        // Once: a return to the cached page keeps what the search found. An elevated relaunch was
        // asked to search, and the user already pressed the button that asked it.
        if (_shown)
        {
            return;
        }

        _shown = true;

        if (ViewModel.IsRequested && ViewModel.SearchCommand.CanExecute(null))
        {
            ViewModel.SearchCommand.Execute(null);
        }
    }

    private void OnDriveListOpened(object sender, object e) => ViewModel.Locations.RefreshDrives();

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window || await FolderDialog.ChooseAsync(window) is not { } picked)
        {
            return;
        }

        ViewModel.Locations.AddFolder(picked);
    }
}
