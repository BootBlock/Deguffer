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
            new ExploreScanner(UserEnvironment.Current.VolumeTables, tuning: App.ScanTuning),
            VolumeInventory.Current,
            WindowsUninstallRegistry.Default,
            UserEnvironment.Current,
            SystemDirectories.Current,
            App.Media);

        var run = new DuplicateSearchRun(
            new DuplicateSearcher(finder, App.Media, App.Checksums),
            MachineProtections.ForThisMachineAsync,
            UserEnvironment.Current,
            CloudFiles.Default,
            VolumeInventory.Current,
            App.Media);

        // Assigned before InitializeComponent so no x:Bind evaluates against a null view-model.
        ViewModel = new DuplicatesViewModel(
            run.RunAsync,

            // The dialog is built per ask: a XamlRoot captured here would be the one from before a
            // theme change or a reparent.
            DuplicateActions.ForThisMachine(() => new ContentDialogDuplicateConfirmation(XamlRoot, ActualTheme), App.Running),
            ChooseCsvAsync,
            App.Preferences,
            new DriveList(VolumeInventory.Current, TimeProvider.System),
            ElevatedRelaunch.IsElevated,
            ElevatedRelaunch.TryRelaunch,
            App.Running,
            App.Work,
            ElevatedRelaunch.Requested as DuplicatesRequest);

        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();

        InitializeComponent();

        ListAnimation.Play(LocationsList, SystemMotion.Current);
        ListAnimation.Play(NotesList, SystemMotion.Current);
        ListAnimation.Play(GroupsList, SystemMotion.Current);
        FillRuleMenu();

        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += OnLoaded;
    }

    public DuplicatesViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Once: a return to the cached page keeps what the search found. An elevated relaunch was
        // asked to search, and the user already pressed the button that asked it.
        if (!_shown)
        {
            _shown = true;

            if (ViewModel.IsRequested)
            {
                ViewModel.SearchCommand.TryExecute();
            }
        }

        // After the search starts, which searches the locations listed and not the drive picker's
        // rows, so it does not wait on the drives being read.
        await ViewModel.Locations.RefreshDrivesAsync(CancellationToken.None);
    }

    private async void OnDriveListOpened(object sender, object e) =>
        await ViewModel.Locations.RefreshDrivesAsync(CancellationToken.None);

    private static Task<string?> ChooseCsvAsync() =>
        App.MainWindow is { } window ? CsvFileDialog.ChooseAsync(window, "Duplicates") : Task.FromResult<string?>(null);

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window || await FolderDialog.ChooseAsync(window) is not { } picked)
        {
            return;
        }

        ViewModel.Locations.AddFolder(picked);
    }

    /// <summary>
    /// One item a rule, in the order the view-model names them. A rule about a folder says so with an
    /// ellipsis, because choosing it opens the folder picker before it marks anything.
    /// </summary>
    private void FillRuleMenu()
    {
        for (var i = 0; i < DuplicateMarkingViewModel.RuleNames.Count; i++)
        {
            var item = new MenuFlyoutItem
            {
                Text = DuplicateMarkingViewModel.RuleNames[i] + (DuplicateMarkingViewModel.RuleAsksForFolder(i) ? "…" : string.Empty),
                Tag = i,
            };
            item.Click += OnRuleChosen;
            RuleMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// Run the rule chosen, asking first for the folder a folder rule is about. A picker closed
    /// without a folder runs nothing, rather than the rule about whatever folder was chosen last.
    /// </summary>
    private async void OnRuleChosen(object sender, RoutedEventArgs e)
    {
        var marking = ViewModel.Marking;
        marking.RuleIndex = (int)((MenuFlyoutItem)sender).Tag;

        if (marking.RuleNeedsFolder)
        {
            if (App.MainWindow is not { } window || await FolderDialog.ChooseAsync(window) is not { } picked)
            {
                return;
            }

            marking.RuleFolder = picked;
        }

        await marking.RunRuleCommand.ExecuteAsync(null);
    }
}
