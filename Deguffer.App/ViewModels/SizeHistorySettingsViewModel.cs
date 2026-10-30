using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution.Sizes;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Settings row that removes the size of each Storage location kept at each scan (#358), beside
/// the scan summaries.
///
/// <para>Its own type for the reason <see cref="ScanHistorySettingsViewModel"/> is: it reads a file
/// when the page is visited, and shares no state with the rest. The history holds no path, but it is
/// still a record of the user's machine, so the user decides how long it stays.</para>
/// </summary>
public sealed partial class SizeHistorySettingsViewModel(ProviderSizeHistory history) : ObservableObject
{
    /// <summary>What the row says it keeps.</summary>
    public static string Description { get; } =
        "After each Storage scan, Deguffer keeps the size of each location it measured, on this computer "
        + "only and with no folder named, so the next scan can show how much each one grew. One size a "
        + $"day is kept for each location, the latest {ProviderSizeStore.KeptPerProvider}.";

    /// <summary>Whether any size is kept, which is whether there is anything to remove.</summary>
    [ObservableProperty]
    public partial bool HasAny { get; private set; }

    /// <summary>Whether the last removal left the history behind, which the page says rather than implying it worked.</summary>
    [ObservableProperty]
    public partial bool RemoveFailed { get; private set; }

    /// <summary>
    /// Ask again whether anything is kept. On a background thread, because the first question reads
    /// the file, and at each visit, because a Storage scan adds to it.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct) => HasAny = await Task.Run(() => history.HasAny, ct);

    /// <summary>Remove every location's kept sizes.</summary>
    public void RemoveAll()
    {
        RemoveFailed = !history.RemoveAll();
        HasAny = history.HasAny;
    }
}
