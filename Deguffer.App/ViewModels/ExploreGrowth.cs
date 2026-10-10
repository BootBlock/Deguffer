using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>One folder in the list of what grew, worded for the panel.</summary>
/// <param name="Node">The folder in the scan on screen, or -1 for one that was removed.</param>
public sealed record ExploreGrowthRow(string Path, string Change, string Size, int Node)
{
    /// <summary>What a screen reader says for the row: the path and the change, in that order.</summary>
    public string Description => $"{Path}: {Change}";

    /// <summary>
    /// The folder's own name, which leads the row because it is what tells two rows apart, or the
    /// whole path for a volume's root, which has no name of its own.
    /// </summary>
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

    /// <summary>The folder holding it, under the name, or empty for a volume's root.</summary>
    public string Parent => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>Whether the row names a folder on screen that the page can open.</summary>
    public bool CanOpen => Node >= 0;
}

/// <summary>One kept scan, as a bar of the used-space strip.</summary>
/// <param name="Share">How full the volume was, 0 to 100, against the largest capacity kept, so the bars rise from a zero baseline.</param>
/// <param name="Description">The date and the figures, for the bar's tooltip and its accessible name.</param>
public sealed record UsedSpaceBar(double Share, string Description);

/// <summary>
/// The Explore page's answer to "what grew since the last scan": the comparison the map is coloured
/// by, the list of folders by growth, and the used space at each kept scan.
///
/// <para>Separate from <see cref="ExploreViewModel"/>, as <see cref="ExploreSelection"/> is, because
/// it holds state of its own that the rest of the page only reads. The decisions in it are Core's:
/// what is compared with what (<see cref="ScanHistory"/>), what is listed
/// (<see cref="ScanGrowth.Listing"/>) and how a change is worded (<see cref="GrowthText"/>).</para>
/// </summary>
public sealed partial class ExploreGrowth : ObservableObject
{
    private readonly ScanHistory _history;

    /// <summary>The volume the comparison is of, so a removal in Settings can be checked against it.</summary>
    private string? _volume;

    public ExploreGrowth(ScanHistory history)
    {
        _history = history;
        _history.Changed += (_, _) => Reconsider();
    }

    /// <summary>
    /// What the map is coloured by when it is coloured by growth, or null where there is nothing to
    /// compare with. It describes one tree, and the map paints nothing as compared in any other.
    /// </summary>
    public ScanGrowth? Comparison { get; private set; }

    /// <summary>The folders that grew most, then those that shrank most. See <see cref="ScanGrowth.Listing"/>.</summary>
    public ObservableCollection<ExploreGrowthRow> Rows { get; } = [];

    /// <summary>The used space at each kept scan of the volume on screen, oldest first.</summary>
    public ObservableCollection<UsedSpaceBar> UsedSpace { get; } = [];

    /// <summary>Which scan the comparison is against, by its date, or empty where there is none.</summary>
    [ObservableProperty]
    public partial string Since { get; private set; } = string.Empty;

    /// <summary>The used space at the first and the last kept scan, in words, or empty.</summary>
    [ObservableProperty]
    public partial string UsedSpaceSummary { get; private set; } = string.Empty;

    /// <summary>
    /// What the panel has to say beyond the list: why nothing is compared, or why the comparison is
    /// approximate, or that this scan could not be kept. Empty where there is nothing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string Note { get; private set; } = string.Empty;

    public bool HasNote => Note.Length > 0;

    /// <summary>
    /// The path of the row that speaks for what the pointer is over on the map, which the panel marks,
    /// or null where no row does. See <see cref="ScanGrowth.ListedFor"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? Pointed { get; private set; }

    /// <summary>Raised when <see cref="Comparison"/> changes, so the map is drawn again.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Mark the row that speaks for <paramref name="node"/> of <paramref name="tree"/>, the shape the
    /// pointer is over on the map, or nothing. A comparison of another tree speaks for nothing in it.
    /// </summary>
    public void Point(ExploreTree? tree, int? node) =>
        Pointed = Comparison is { } growth && ReferenceEquals(growth.Tree, tree) && node is { } over
            ? growth.ListedFor(over)?.Path
            : null;

    /// <summary>Show what recording a finished scan produced.</summary>
    public void Show(ScanRecord record)
    {
        if (GrowthText.Why(record.NotKept) is { } why)
        {
            Clear();
            Note = why;
            return;
        }

        _volume = record.Volume;
        Comparison = record.Growth;

        var notes = new List<string>();

        if (record.Growth is { } growth)
        {
            Since = $"Compared with the scan of {Local(growth.SinceUtc)}";

            if (GrowthText.Approximate(growth.Approximation) is { } approximate)
            {
                notes.Add(approximate);
            }

            // An empty list with nothing said reads as a panel that failed to fill.
            if (growth.Listing().Count == 0)
            {
                notes.Add(GrowthText.NothingChanged);
            }
        }
        else
        {
            Since = string.Empty;
            notes.Add(GrowthText.NothingEarlier);
        }

        if (!record.Saved)
        {
            notes.Add(GrowthText.NotSaved);
        }

        Note = string.Join(" ", notes);
        ShowRows();
        ShowUsedSpace(record.Kept);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Show nothing: a scan has started, or was cancelled, and nothing on screen is compared.</summary>
    public void Clear()
    {
        _volume = null;
        Comparison = null;
        Since = string.Empty;
        Note = string.Empty;
        UsedSpaceSummary = string.Empty;
        Rows.Clear();
        UsedSpace.Clear();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Follow a removal in Settings. A comparison with a summary that is no longer kept stops, because
    /// the user asked for it to be gone, and the strip loses the bars that went with it.
    /// </summary>
    private void Reconsider()
    {
        if (_volume is not { } volume)
        {
            return;
        }

        if (Comparison is { } growth && !_history.StillKept(growth))
        {
            Comparison = null;
            Since = string.Empty;
            Note = GrowthText.NothingEarlier;
            Rows.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        ShowUsedSpace(_history.Kept(volume));
    }

    private void ShowRows()
    {
        if (Comparison is not { } growth)
        {
            Rows.Clear();
            return;
        }

        var floor = growth.Earlier.UnrecordedAtMost;

        ExploreGrowthRow Row(FolderChange change) => new(
            change.Path,
            GrowthText.Change(change, floor),
            change.Kind == FolderChangeKind.Removed ? string.Empty : FreeSpace.Format(change.After),
            change.Node);

        LiveList.Show(Rows, [.. growth.Listing().Select(Row)], row => row.Path);
    }

    private void ShowUsedSpace(IReadOnlyList<KeptSummary> kept)
    {
        var capacity = kept.Count > 0 ? kept.Max(summary => summary.TotalBytes) : 0;

        LiveList.Rewrite(
            UsedSpace,
            [
                .. kept.Select(summary => new UsedSpaceBar(
                    capacity > 0 ? 100.0 * summary.UsedBytes / capacity : 0,
                    $"{Local(summary.TakenUtc)}: {FreeSpace.Format(summary.UsedBytes)} used of "
                    + $"{FreeSpace.Format(summary.TotalBytes)}")),
            ]);

        UsedSpaceSummary = kept.Count switch
        {
            0 => string.Empty,
            1 => $"Used space at the one kept scan: {FreeSpace.Format(kept[0].UsedBytes)}",
            _ => $"Used space at each kept scan, from {FreeSpace.Format(kept[0].UsedBytes)} on "
                + $"{Local(kept[0].TakenUtc)} to {FreeSpace.Format(kept[^1].UsedBytes)} on {Local(kept[^1].TakenUtc)}",
        };
    }

    /// <summary>A date in the user's own format, at the last moment before a person reads it.</summary>
    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("g");
}
