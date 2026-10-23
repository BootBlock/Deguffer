using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Memory;
using Deguffer.Core.Startup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Deguffer.App.Views;

/// <summary>
/// The Startup destination (§7.5). Wires the view-model to the machine and carries the list's
/// selection to it; every decision is Core's.
/// </summary>
public sealed partial class StartupPage : Page
{
    /// <summary>Windows' own Startup page in Settings, which Microsoft documents as a launch target.</summary>
    private static readonly Uri SettingsStartup = new("ms-settings:startupapps");

    private bool _hasRead;

    public StartupPage()
    {
        var switcher = new StartupSwitcher(
            WindowsStartupRegistry.Default,
            StartupFolderReader.Current,
            TimeProvider.System);

        // Assigned before InitializeComponent so no x:Bind evaluates against a null view-model.
        ViewModel = new StartupViewModel(
            ct => StartupView.Read(StartupReader.Default, MemorySource.Default, ProcessImages.Default, ct),
            entry => switcher.Switch(entry, ElevatedRelaunch.IsElevated),
            ElevatedRelaunch.IsElevated,
            ElevatedRelaunch.TryRelaunch,
            async () => await Launcher.LaunchUriAsync(SettingsStartup),
            App.Running,
            App.Work);

        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();
        ViewModel.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(StartupViewModel.SelectedRow) && !ReferenceEquals(EntryList.SelectedItem, ViewModel.SelectedRow))
            {
                // A link from Memory picked it, so the list follows and brings it into view.
                EntryList.SelectedItem = ViewModel.SelectedRow;

                if (ViewModel.SelectedRow is { } row)
                {
                    EntryList.ScrollIntoView(row);
                }
            }
        };

        InitializeComponent();

        ListAnimation.Play(EntryList, SystemMotion.Current);

        NavigationCacheMode = NavigationCacheMode.Required;

        Loaded += OnLoaded;
    }

    public StartupViewModel ViewModel { get; }

    /// <summary>A link from Memory names the entry to pick, which the reading picks once it holds it.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is StartupKey key)
        {
            ViewModel.Reveal(key);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Once: a return to the cached page keeps the list the reader left, and Refresh reads again.
        if (_hasRead)
        {
            return;
        }

        _hasRead = true;
        _ = ViewModel.RefreshAsync();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SelectedRow = EntryList.SelectedItem as StartupRow;

    private void OnReportClosed(InfoBar sender, object args) => ViewModel.DismissReportCommand.Execute(null);
}
