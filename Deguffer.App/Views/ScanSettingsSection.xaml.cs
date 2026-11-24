using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The Settings page's scanning section, folded away behind an expander. It owns the listing of the
/// drives present. Its view-model comes from the page, which builds it with the page's one notice of a
/// failed save, so a failure here and a save elsewhere on the page are one notice.
/// </summary>
public sealed partial class ScanSettingsSection : UserControl
{
    public static readonly DependencyProperty ScanningProperty =
        DependencyProperty.Register(nameof(Scanning), typeof(ScanSettingsViewModel), typeof(ScanSettingsSection), new PropertyMetadata(null, OnScanningChanged));

    /// <summary>The listing of drives for the visit under way, stopped when the section is left.</summary>
    private CancellationTokenSource? _visit;

    public ScanSettingsSection()
    {
        InitializeComponent();

        ListAnimation.Play(DrivesList, SystemMotion.Current);

        Loaded += OnLoaded;
        Unloaded += (_, _) => _visit?.Cancel();
    }

    /// <summary>Set once, by the page, as it binds.</summary>
    public ScanSettingsViewModel? Scanning
    {
        get => (ScanSettingsViewModel?)GetValue(ScanningProperty);
        set => SetValue(ScanningProperty, value);
    }

    // The section's own bindings are read again whichever is first, its loading or the page's binding.
    private static void OnScanningChanged(DependencyObject sender, DependencyPropertyChangedEventArgs change) =>
        ((ScanSettingsSection)sender).Bindings.Update();

    /// <summary>
    /// At each visit rather than once, so a drive attached while the app was open is listed. Listed
    /// while the section is closed too, so opening it shows the drives at once.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Scanning is not { } scanning)
        {
            return;
        }

        _visit?.Cancel();
        _visit?.Dispose();
        _visit = new CancellationTokenSource();

        try
        {
            await scanning.RefreshDrivesAsync(_visit.Token);
        }
        catch (OperationCanceledException)
        {
            // The page was left, or visited again, before every drive had answered. The next visit
            // lists them afresh.
        }
    }
}
