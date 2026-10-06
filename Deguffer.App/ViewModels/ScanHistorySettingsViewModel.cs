using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>One kept scan summary, worded for its row in Settings.</summary>
public sealed record KeptScanRow(KeptSummary Summary)
{
    /// <summary>The drive as it was mounted, and when it was scanned, in the user's own format.</summary>
    public string Title => $"{Summary.RootPath}  {Summary.TakenUtc.ToLocalTime():g}";

    /// <summary>What the drive held then, and how it was read, because that decides how far it compares.</summary>
    public string Detail =>
        $"{FreeSpace.Format(Summary.UsedBytes)} used of {FreeSpace.Format(Summary.TotalBytes)}, "
        + (Summary.Strategy == ScanStrategy.MasterFileTable ? "read from the file table" : "read by walking folders")
        + (Summary.LowerBound ? ", with parts that could not be read" : string.Empty);
}

/// <summary>
/// The Settings section that lists the kept scan summaries and removes them (#260).
///
/// <para>Its own type beside <see cref="SettingsViewModel"/>, as <see cref="ScanSettingsViewModel"/>
/// is: it reads files when the page is visited, which nothing else there does, and shares no state
/// with the rest.</para>
///
/// <para>A summary holds the names and sizes of the folders on a drive, so the user is the one who
/// decides how long it stays, and removing one removes it from every comparison at once.</para>
/// </summary>
public sealed partial class ScanHistorySettingsViewModel(ScanHistory history) : ObservableObject
{
    /// <summary>Every kept summary of every drive, newest first.</summary>
    public ObservableCollection<KeptScanRow> Kept { get; } = [];

    public bool HasNoKept => Kept.Count == 0;

    public bool HasKept => Kept.Count > 0;

    /// <summary>Whether the last removal left something behind, which the page says rather than implying it worked.</summary>
    [ObservableProperty]
    public partial bool RemoveFailed { get; private set; }

    /// <summary>
    /// Read the kept summaries again. On a background thread, because each summary is a file, and at
    /// each visit, because a scan on the Explore page adds one.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var all = await Task.Run(history.All, ct);

        Show(all);
    }

    /// <summary>Remove <paramref name="row"/>'s summary.</summary>
    public void Remove(KeptScanRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        RemoveFailed = !history.Remove(row.Summary);
        Show(history.All());
    }

    /// <summary>Remove every kept summary of every drive.</summary>
    public void RemoveAll()
    {
        RemoveFailed = !history.RemoveAll();
        Show(history.All());
    }

    private void Show(IReadOnlyList<KeptSummary> all)
    {
        LiveList.Show(Kept, [.. all.Select(summary => new KeptScanRow(summary))], row => row.Summary);

        OnPropertyChanged(nameof(HasNoKept));
        OnPropertyChanged(nameof(HasKept));
    }
}
