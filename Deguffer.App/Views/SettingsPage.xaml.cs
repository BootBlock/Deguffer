using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Deguffer.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(App.Preferences, App.SourceRoots, App.Keeps);
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

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
        if (App.MainWindow is not { } window)
        {
            return;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        // A picker with no owner throws in a WinUI 3 desktop app rather than opening unowned.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        if (await picker.PickSingleFolderAsync() is not { } folder)
        {
            return;
        }

        var approval = ViewModel.ApprovalFor(folder.Path);

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
