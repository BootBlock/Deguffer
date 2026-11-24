using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The Settings page's scanning section, folded away behind an expander. It owns its view-model and
/// the listing of the drives present, so the page holds no part of it.
/// </summary>
public sealed partial class ScanSettingsSection : UserControl
{
    /// <summary>The listing of drives for the visit under way, stopped when the section is left.</summary>
    private CancellationTokenSource? _visit;

    public ScanSettingsSection()
    {
        Scanning = new ScanSettingsViewModel(App.Preferences, App.ScanTuning, VolumeInventory.Current);
        InitializeComponent();

        ListAnimation.Play(DrivesList, SystemMotion.Current);

        Loaded += OnLoaded;
        Unloaded += (_, _) => _visit?.Cancel();
    }

    public ScanSettingsViewModel Scanning { get; }

    /// <summary>
    /// At each visit rather than once, so a drive attached while the app was open is listed. Listed
    /// while the section is closed too, so opening it shows the drives at once.
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
            // The page was left, or visited again, before every drive had answered. The next visit
            // lists them afresh.
        }
    }
}
