using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Deguffer.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(App.Preferences, App.SourceRoots);
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

        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            ViewModel.AddSourceRoot(folder.Path);
        }
    }

    private void OnRemoveSourceRoot(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string root })
        {
            ViewModel.RemoveSourceRoot(root);
        }
    }
}
