using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Deguffer.App.Views;

/// <summary>
/// The Installed apps destination (§7.3). Wires the view-model to the machine and carries the
/// lists' selections to it; every decision is Core's.
/// </summary>
public sealed partial class InstalledAppsPage : Page
{
    public InstalledAppsPage()
    {
        var registry = WindowsUninstallRegistry.Default;
        var backups = RegistryBackups.For(UserEnvironment.Current, SystemDirectories.Current, ProcessRunner.Default);
        var reader = InstalledAppsReader.Default;

        // Assigned before InitializeComponent so no x:Bind evaluates against a null view-model.
        ViewModel = new InstalledAppsViewModel(
            reader.Read,
            new InstalledAppsActions(
                new EntryRemover(registry, reader, backups),
                new BackupRestorer(registry, backups),
                new ProgramUninstaller(reader, ShellUninstallLauncher.Default, NativeSystemTool.In(SystemDirectories.Current, "msiexec.exe")),
                backups,

                // Built per ask, so the dialog never holds a XamlRoot from before a theme change.
                () => new ContentDialogInstalledAppsConfirmation(XamlRoot, ActualTheme),
                App.Preferences,
                ElevatedRelaunch.IsElevated,
                App.Running),
            ElevatedRelaunch.IsElevated,
            ElevatedRelaunch.TryRelaunch,
            App.Running);

        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();

        InitializeComponent();

        ListAnimation.Play(StaleList, SystemMotion.Current);
        ListAnimation.Play(InstalledList, SystemMotion.Current);
        ListAnimation.Play(BackupList, SystemMotion.Current);

        NavigationCacheMode = NavigationCacheMode.Required;
        BackupsIntro = $"Backups Deguffer took before removing an entry, kept in {ViewModel.Actions.BackupFolder}. "
            + "Restoring one writes the entry back with reg.exe, and only where the entry is not already there.";

        Loaded += OnLoaded;
    }

    public InstalledAppsViewModel ViewModel { get; }

    public string BackupsIntro { get; }

    private bool _hasRead;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Once: a return to the cached page keeps the list the reader left, and Refresh reads again.
        if (_hasRead)
        {
            return;
        }

        _hasRead = true;
        ViewModel.Actions.ShowBackups();
        _ = ViewModel.RefreshAsync();
    }

    private void OnStaleSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Actions.SelectStale([.. StaleList.SelectedItems.OfType<InstalledAppRow>().Select(r => r.Entry)]);

    private void OnInstalledSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Actions.SelectInstalled([.. InstalledList.SelectedItems.OfType<InstalledAppRow>().Select(r => r.Entry)]);

    private void OnBackupSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Actions.SelectedBackup = BackupList.SelectedItem as BackupRow;

    private void OnReportClosed(InfoBar sender, object args) => ViewModel.Actions.DismissReportCommand.Execute(null);
}
