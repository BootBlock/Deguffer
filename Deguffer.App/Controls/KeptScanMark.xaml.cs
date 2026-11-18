using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// The line over the Explore map while it draws the last kept scan of the drive being scanned, which
/// says when that scan was taken and that the drive is being scanned again (#491).
/// </summary>
public sealed partial class KeptScanMark
{
    public static readonly DependencyProperty ScanProperty = DependencyProperty.Register(
        nameof(Scan),
        typeof(ExploreKeptScan),
        typeof(KeptScanMark),
        new PropertyMetadata(null));

    public KeptScanMark() => InitializeComponent();

    /// <summary>The kept scan the page shows, whose line this states.</summary>
    public ExploreKeptScan? Scan
    {
        get => (ExploreKeptScan?)GetValue(ScanProperty);
        set => SetValue(ScanProperty, value);
    }
}
