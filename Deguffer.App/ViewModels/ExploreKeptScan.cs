using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.History;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The last kept scan of the drive the Explore page is scanning, which the map draws in place of the
/// scan until the scan has a tree of its own (#491).
///
/// <para>Separate from <see cref="ExploreViewModel"/>, as <see cref="ExploreReplay"/> is, because it
/// holds state of its own: which scan it is for, and what was found for it. The decisions are Core's:
/// when it is shown (<see cref="KeptScanShown"/>), what it draws (<see cref="KeptScanTree"/>) and what
/// the page says over it (<see cref="KeptScanText"/>).</para>
/// </summary>
public sealed partial class ExploreKeptScan(ScanHistory history, TimeProvider time) : ObservableObject
{
    /// <summary>How many scans the page has started, which numbers each for <see cref="_shown"/>.</summary>
    private int _scans;

    private KeptScanShown _shown;

    /// <summary>The kept scan on screen, or null.</summary>
    public KeptScanTree? Tree => _shown.Tree;

    /// <summary>The line over the map while it shows <see cref="Tree"/>. See <see cref="KeptScanText.Mark"/>.</summary>
    public string Mark => Tree is { } kept ? KeptScanText.Mark(kept.Summary, time.GetUtcNow().UtcDateTime) : string.Empty;

    /// <inheritdoc cref="KeptScanText.Help"/>
    public string Help => KeptScanText.Help;

    /// <summary>
    /// The look for the kept scan of the scan under way, which the scan awaits as it ends, so a fault
    /// in it is never lost. Internal for a test that acts once it has answered.
    /// </summary>
    internal Task Lookup { get; private set; } = Task.CompletedTask;

    /// <summary>A kept scan came on screen, so the page takes away whatever tree was there and draws it.</summary>
    public event EventHandler? Arrived;

    /// <summary>
    /// A scan of <paramref name="target"/> starts, with <paramref name="onScreen"/> on the page and
    /// <paramref name="onScreenVolume"/> beside it, and <paramref name="scanned"/> reading its volume.
    /// Looks for the kept scan beside the scan rather than before it: the scan does not wait for it.
    /// </summary>
    public void Begin(string target, ExploreTree? onScreen, VolumeSpace onScreenVolume, ScannedVolume scanned)
    {
        Replace();
        _shown = KeptScanShown.Begin(++_scans, target, onScreen, onScreenVolume);
        Lookup = _shown.Looking ? LookAsync(_shown.Scan, target, scanned) : Task.CompletedTask;
    }

    /// <summary>
    /// The scan has a tree of its own, or has ended, so nothing is shown and nothing found later is.
    /// Returns whether a kept scan was on screen, which the page then draws without.
    /// </summary>
    public bool Replace()
    {
        var drawn = _shown.Shows;
        _shown = _shown.Replaced();

        if (drawn)
        {
            Said();
        }

        return drawn;
    }

    /// <summary>
    /// Find the last kept scan of the whole volume scan number <paramref name="scan"/> covers, off the
    /// window's thread, and show it where <see cref="_shown"/> is still looking for that scan. Nothing
    /// where the scan covers a folder or nothing of the volume is kept.
    /// </summary>
    private async Task LookAsync(int scan, string target, ScannedVolume scanned)
    {
        var kept = await scanned.WholeVolumeNameAsync() is { } name
            ? await Task.Run(() => history.Latest(name) is { } summary ? KeptScanTree.Of(summary, target) : null)
            : null;

        _shown = _shown.Offered(scan, kept);

        if (ReferenceEquals(_shown.Tree, kept) && kept is not null)
        {
            Said();
            Arrived?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Said()
    {
        OnPropertyChanged(nameof(Tree));
        OnPropertyChanged(nameof(Mark));
    }
}
