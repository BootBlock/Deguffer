using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Health;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Deguffer.App.Views;

/// <summary>
/// The Health destination (§7.6). Built on the first visit and then kept, so nothing asks a drive
/// anything until the reader comes here, and a return shows what they left. Every decision is Core's.
/// </summary>
public sealed partial class HealthPage : Page
{
    private readonly ListSelectionGuard _guard;

    public HealthPage()
    {
        var reader = new DriveHealthReader(DriveProbe.Machine, VolumeInventory.Current, StorageQueries.Machine, DriveHealthReader.DefaultAnswerLimit);

        // Assigned before InitializeComponent so no x:Bind evaluates against a null view-model.
        ViewModel = new HealthViewModel(
            reader.List,
            reader.ReadAsync,
            ElevatedRelaunch.IsElevated,
            ElevatedRelaunch.TryRelaunch,
            App.Running,
            App.Work);

        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();

        InitializeComponent();

        NavigationCacheMode = NavigationCacheMode.Required;

        _guard = new ListSelectionGuard(() => false, restore => DispatcherQueue.TryEnqueueGuarded(restore));
        _guard.Hear(DriveList);

        ViewModel.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(HealthViewModel.SelectedDrive))
            {
                ShowSelectedDrive();
            }
        };

        Loaded += (_, _) => ViewModel.EnsureRead();
    }

    public HealthViewModel ViewModel { get; }

    private void ShowSelectedDrive() => _guard.Write(() => DriveList.SelectedItem = ViewModel.SelectedDrive);

    /// <summary>
    /// A list held by the navigation cache builds its containers again on a return and reports a
    /// selection nobody made, so only a selection the reader made is taken.
    /// </summary>
    private void OnDriveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_guard.Admits(restore: ShowSelectedDrive))
        {
            ViewModel.SelectedDrive = DriveList.SelectedItem as HealthDriveRow;
        }
    }
}
