using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        // One notice of a failed save for the whole page, so the scanning section and the rest of the
        // page never state one failure twice, and a save in either clears it.
        var latest = new LatestSave();
        ViewModel = new SettingsViewModel(
            App.Preferences,
            App.SourceRoots,
            App.EmulatorFolders,
            App.Keeps,
            new SourceFolderAddition(App.SourceRoots, VolumeInventory.Current, () => new ContentDialogSourceFolderPrompt(this)),
            latest);
        Scanning = new ScanSettingsViewModel(App.Preferences, App.ScanTuning, VolumeInventory.Current, latest);
        Restore = new SettingsRestore(App.Preferences, latest);
        History = new ScanHistorySettingsViewModel(App.ScanHistory);
        Sizes = new SizeHistorySettingsViewModel(App.ProviderSizes);
        InitializeComponent();

        ListAnimation.Play(SourceRootsList, SystemMotion.Current);
        ListAnimation.Play(EmulatorFoldersList, SystemMotion.Current);
        ListAnimation.Play(KeptItemsList, SystemMotion.Current);
        ListAnimation.Play(KeptScansList, SystemMotion.Current);

        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _visit?.Cancel();
            App.Preferences.Changed -= OnPreferencesChanged;
        };
    }

    /// <summary>
    /// The preview follows the stored preferences rather than the box, so it shows what took effect:
    /// a spacing that could not be saved puts the box back, and the preview with it. The box follows
    /// them too, as the map appearance window writes the same spacing while this page is open.
    /// </summary>
    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        ViewModel.FollowStoredSpacing();
        ShowSpacing();

        // Each row's default and the page's restore follow every write, made by either form or by the
        // restore, as no one of them sees the others'.
        ViewModel.Defaults.Follow();
        Scanning.Defaults.Follow();
        Restore.Follow();
    }

    /// <summary>
    /// Ask before every setting on the page goes back to its default, naming each one that will change,
    /// then put them back and have both parts of the page read what was stored.
    /// </summary>
    private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        if (Restore.Prompt() is not { } prompt)
        {
            return;
        }

        var dialog = DialogFactory.Question(this, prompt.Title, DialogFactory.Listing(prompt.Consequence, prompt.Items), prompt.ConfirmLabel);

        if (await ModalDialog.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        Restore.Restore();
        ViewModel.ShowStored();
        Scanning.ShowStored();
    }

    private void ShowSpacing() => SpacingPreview.Show(MapLook.From(App.Preferences.Current), ExploreView.Treemap);

    /// <summary>The reading of the histories for the visit under way, stopped when the page is left.</summary>
    private CancellationTokenSource? _visit;

    /// <summary>
    /// At each visit rather than once, so a scan finished since the last visit is listed.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowSpacing();
        App.Preferences.Changed += OnPreferencesChanged;

        _visit?.Cancel();
        _visit?.Dispose();
        _visit = new CancellationTokenSource();

        try
        {
            await Task.WhenAll(History.RefreshAsync(_visit.Token), Sizes.RefreshAsync(_visit.Token));
        }
        catch (OperationCanceledException)
        {
            // The page was left, or visited again, before both histories were read. The next visit
            // reads them afresh.
        }
    }

    public SettingsViewModel ViewModel { get; }

    public ScanSettingsViewModel Scanning { get; }

    public SettingsRestore Restore { get; }

    public ScanHistorySettingsViewModel History { get; }

    public SizeHistorySettingsViewModel Sizes { get; }

    private void OnOpenMapAppearance(object sender, RoutedEventArgs e) => App.ShowMapAppearance();

    private void OnRemoveScanSummary(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: KeptScanRow row })
        {
            History.Remove(row);
        }
    }

    private void OnRemoveAllScanSummaries(object sender, RoutedEventArgs e) => History.RemoveAll();

    private void OnRemoveSizeHistory(object sender, RoutedEventArgs e) => Sizes.RemoveAll();

    /// <summary>
    /// The picker and the warning are <see cref="ContentDialogSourceFolderPrompt"/>'s, handed to the
    /// view-model at construction, so this handler only starts the request.
    /// </summary>
    private async void OnAddSourceRoot(object sender, RoutedEventArgs e) =>
        await ViewModel.AddSourceRootAsync(CancellationToken.None);

    /// <summary>
    /// An emulator folder goes through the picker for the reason a source folder does. No warning is
    /// needed first: Deguffer looks in it only for the settings file each emulator writes, and takes
    /// nothing from it unless one is there.
    /// </summary>
    private async void OnAddEmulatorFolder(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } folder)
        {
            ViewModel.AddEmulatorFolder(folder);
        }
    }

    private void OnRemoveEmulatorFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string folder })
        {
            ViewModel.RemoveEmulatorFolder(folder);
        }
    }

    /// <summary>The folder the user picked, or null if they cancelled or there is no window to own the picker.</summary>
    private static async Task<string?> PickFolderAsync() =>
        App.MainWindow is { } window ? await FolderDialog.ChooseAsync(window) : null;

    private void OnRemoveSourceRoot(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string root })
        {
            ViewModel.RemoveSourceRoot(root);
        }
    }

    private void OnReleaseKeptItem(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: KeptItem item })
        {
            ViewModel.ReleaseKeptItem(item);
        }
    }
}
