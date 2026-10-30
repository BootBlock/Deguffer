using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Sizes;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>One kept figure of one provider, worded for its line in the provider's size history.</summary>
/// <param name="When">When the scan that measured it ran, in the user's own format.</param>
/// <param name="Size">What the row stated then.</param>
/// <param name="Change">How far it moved from the figure before it, or empty for the oldest.</param>
/// <param name="Share">The figure against the largest one kept, 0 to 100, so the bars rise from a zero baseline.</param>
public sealed record ProviderSizeRow(string When, string Size, string Change, double Share)
{
    /// <summary>What a bar's tooltip and a screen reader say for it.</summary>
    public string Description => Change.Length > 0 ? $"{When}: {Size}, {Change}" : $"{When}: {Size}";
}

/// <summary>
/// The Storage page's side of the provider size history (#358): each row's change since the last scan,
/// and what each scan measured kept for the next.
///
/// <para>Apart from <see cref="CleanViewModel"/>, which runs the scans, because this holds state of
/// its own across one: the figures from before it, and what it measured so far. The decisions are
/// Core's: what counts as a figure (<see cref="SizePoint.Of"/>), how it is kept
/// (<see cref="ProviderSizeHistory"/>) and how a change is worded (<see cref="SizeChange"/>).</para>
/// </summary>
public sealed class StorageSizeChanges
{
    private readonly ProviderSizeHistory _history;
    private readonly IReadOnlyList<FindingViewModel> _rows;
    private readonly bool _isElevated;
    private readonly List<Finding> _measured = [];

    /// <summary>The newest figure of each provider from before the scan under way.</summary>
    private IReadOnlyDictionary<string, SizePoint> _before = new Dictionary<string, SizePoint>();

    /// <summary>When the scan under way started, which is the time every figure it measures is kept at.</summary>
    private DateTime _takenUtc;

    /// <summary>What a provider's size history says about what it keeps, and why a change can be approximate.</summary>
    public static string HistoryNote { get; } =
        $"One size a day is kept, the latest, for up to {ProviderSizeStore.KeptPerProvider} days with a scan. "
        + "A scan with administrator rights can read folders that one without them cannot, so a change "
        + "between the two is approximate. Settings removes this history.";

    /// <param name="rows">The page's rows, whose changes go when the history is removed.</param>
    /// <param name="isElevated">Whether this process holds administrator rights, which each figure records.</param>
    public StorageSizeChanges(ProviderSizeHistory history, IReadOnlyList<FindingViewModel> rows, bool isElevated)
    {
        _history = history;
        _rows = rows;
        _isElevated = isElevated;
        _history.Changed += (_, _) => Forget();
    }

    /// <summary>
    /// Start a scan: take the newest figure of each provider, so every row it lands is compared with
    /// what was there before it. On a worker, because the first question reads the file.
    /// </summary>
    public async Task BeginAsync(CancellationToken ct)
    {
        _measured.Clear();
        _takenUtc = DateTime.UtcNow;
        _before = await Task.Run(_history.Latest, ct);
    }

    /// <summary>State <paramref name="row"/>'s change since the scan before, as its finding lands.</summary>
    public void Show(FindingViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        _measured.Add(row.Finding);

        var change = SizePoint.Of(row.Finding, _takenUtc, _isElevated) is { } now
            ? SizeChange.Between(_before.TryGetValue(row.Finding.Provider.Id, out var then) ? then : null, now)
            : null;

        row.ChangeLabel = change?.Describe(Local) ?? string.Empty;
    }

    /// <summary>
    /// Keep what the scan measured, whether or not it ran to its end: each figure that landed is a
    /// measurement of its own, and one a cancelled scan reached is as true as one a whole scan did.
    /// </summary>
    public Task EndAsync()
    {
        Finding[] measured = [.. _measured];
        _measured.Clear();

        return measured.Length == 0
            ? Task.CompletedTask
            : Task.Run(() => _history.Record(measured, _takenUtc, _isElevated));
    }

    /// <summary>The figures kept for <paramref name="row"/>'s provider, oldest first.</summary>
    public IReadOnlyList<ProviderSizeRow> HistoryOf(FindingViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var points = _history.Of(row.Finding.Provider.Id);
        var largest = points.Count > 0 ? points.Max(point => point.Bytes) : 0;

        return
        [
            .. points.Select((point, at) => new ProviderSizeRow(
                Local(point.TakenUtc),
                point.Approximate ? $"about {FreeSpace.Format(point.Bytes)}" : FreeSpace.Format(point.Bytes),
                at > 0 && SizeChange.Between(points[at - 1], point) is { } change ? change.Amount : string.Empty,
                largest > 0 ? 100.0 * point.Bytes / largest : 0)),
        ];
    }

    /// <summary>
    /// Follow a removal in Settings. A change against a history the user asked to be gone stops being
    /// stated, and the scan under way compares with nothing.
    /// </summary>
    private void Forget()
    {
        _before = new Dictionary<string, SizePoint>();

        foreach (var row in _rows)
        {
            row.ChangeLabel = string.Empty;
        }
    }

    /// <summary>A date and time in the user's own format, at the last moment before a person reads it.</summary>
    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("g");
}
