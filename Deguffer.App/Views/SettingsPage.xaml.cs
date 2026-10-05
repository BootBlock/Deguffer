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
        ViewModel = new SettingsViewModel(
            App.Preferences, App.SourceRoots, App.EmulatorFolders, App.Keeps, VolumeInventory.Current);
        Scanning = new ScanSettingsViewModel(App.Preferences, App.ScanTuning, VolumeInventory.Current);
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += (_, _) => _visit?.Cancel();
    }

    /// <summary>The listing of drives for the visit under way, stopped when the page is left.</summary>
    private CancellationTokenSource? _visit;

    /// <summary>
    /// At each visit rather than once, so a drive attached while the app was open is listed.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _visit?.Cancel();
        _visit?.Dispose();
        _visit = new CancellationTokenSource();

        try
        {
            await Scanning.RefreshDrivesAsync(_visit.Token);
        }
        catch (OperationCanceledException)
        {
            // The page was left, or visited again, before every drive had answered. The next
            // visit lists them afresh.
        }
    }

    public SettingsViewModel ViewModel { get; }

    public ScanSettingsViewModel Scanning { get; }

    /// <summary>
    /// Approving a folder goes through the system picker rather than a text box, so the path is one
    /// the user navigated to and the shell confirmed exists. This is the only setting that widens
    /// what Deguffer will delete, which is reason enough not to accept a typed string.
    ///
    /// The picking lives here rather than in the view model: it is a WinUI dialog needing a window
    /// handle, and the view model stays testable by knowing only about the path that comes back.
    /// </summary>
    private async void OnAddSourceRoot(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is not { } folder || ViewModel.ApprovalFor(folder) is not { } approval)
        {
            return;
        }

        if (approval.NeedsConfirming && !await AcceptsAsync(approval))
        {
            return;
        }

        ViewModel.AddSourceRoot(approval);
    }

    /// <summary>
    /// Ask before approving a folder Deguffer has something to warn about, and report whether the user
    /// said yes.
    ///
    /// <para>A dialog rather than a sentence on the row afterwards. The warning is about a cost the
    /// folder's own contents decide — the user knows what is in it and Deguffer does not — so it has to
    /// be read before the approval, not discovered after it. Cancel is the default button, on
    /// <see cref="ContentDialogExploreConfirmation"/>'s reasoning: a reader who presses Enter on a
    /// dialog they have not read must not consent by reflex.</para>
    /// </summary>
    private async Task<bool> AcceptsAsync(SourceRootApproval approval)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,

            // The popup layer does not inherit the theme applied to the window root, so without this
            // it renders dark over a light window.
            RequestedTheme = ActualTheme,

            Title = "Add this folder anyway?",
            Content = new TextBlock
            {
                Text = $"{approval.Path}\n\n{approval.Warning}",
                TextWrapping = TextWrapping.WrapWholeWords,
            },
            PrimaryButtonText = "Add folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await ModalDialog.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

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
