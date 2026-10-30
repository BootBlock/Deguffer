namespace Deguffer.Core.Execution.Sizes;

/// <summary>
/// The size of each provider at each Storage scan (#358), so a row can say how far its cause grew
/// since the last scan, and show its own history on request.
///
/// <para><b>From the figures the scan already measured, never measured again.</b> G4: a scan has just
/// walked every location, and what it found is what is kept. Nothing here reads the disk but the one
/// file <see cref="ProviderSizeStore"/> keeps.</para>
///
/// <para><b>One figure per provider per day, the latest.</b> A user who scans, cleans and scans again
/// in an afternoon would otherwise spend the history in a week. The change a row states is against the
/// newest figure before its scan, which this keeps however often that is.</para>
///
/// <para>Shared by every caller, as one instance, for the reason <see cref="Exploring.History.ScanHistory"/>
/// is: two would disagree about what is kept the moment either removed it. The file is read once, at
/// the first question, and held for the life of the app.</para>
/// </summary>
public sealed class ProviderSizeHistory(ProviderSizeStore store)
{
    private readonly Lock _gate = new();

    private Dictionary<string, IReadOnlyList<SizePoint>>? _kept;

    /// <summary>
    /// Raised after the history is removed, so a page stating a change against it can stop. Raised on
    /// the thread that removed it.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Whether any provider has a figure kept.</summary>
    public bool HasAny
    {
        get
        {
            lock (_gate)
            {
                return KeptLocked().Count > 0;
            }
        }
    }

    /// <summary>The newest figure of every provider that has one.</summary>
    public IReadOnlyDictionary<string, SizePoint> Latest()
    {
        lock (_gate)
        {
            return KeptLocked().ToDictionary(entry => entry.Key, entry => entry.Value[^1], StringComparer.Ordinal);
        }
    }

    /// <summary>The figures kept for the provider <paramref name="providerId"/>, oldest first.</summary>
    public IReadOnlyList<SizePoint> Of(string providerId)
    {
        lock (_gate)
        {
            return KeptLocked().GetValueOrDefault(providerId) ?? [];
        }
    }

    /// <summary>
    /// Keep what a scan at <paramref name="takenUtc"/> measured of each of <paramref name="findings"/>
    /// (<see cref="SizePoint.Of"/>). Returns whether it was stored.
    /// </summary>
    /// <param name="elevated">Whether the scan held administrator rights.</param>
    public bool Record(IEnumerable<Finding> findings, DateTime takenUtc, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(findings);

        lock (_gate)
        {
            var kept = KeptLocked();
            var recorded = false;

            foreach (var finding in findings)
            {
                if (SizePoint.Of(finding, takenUtc, elevated) is { } point)
                {
                    kept[finding.Provider.Id] = With(kept.GetValueOrDefault(finding.Provider.Id) ?? [], point);
                    recorded = true;
                }
            }

            return recorded && store.Save(kept);
        }
    }

    /// <summary>Remove every provider's history. Returns whether it is gone.</summary>
    public bool RemoveAll()
    {
        bool removed;

        lock (_gate)
        {
            removed = store.Remove();

            // What is held goes either way. A file the user asked to remove is not one to go on
            // comparing with, and one that would not go is read again at the next question.
            _kept = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return removed;
    }

    /// <summary>
    /// <paramref name="points"/> with <paramref name="point"/> added: in place of a figure from the
    /// same day, and at most <see cref="ProviderSizeStore.KeptPerProvider"/> of them, the oldest going
    /// first. A figure no later than the newest is a clock that moved back, and it replaces what is
    /// newer rather than sitting out of order.
    /// </summary>
    private static IReadOnlyList<SizePoint> With(IReadOnlyList<SizePoint> points, SizePoint point)
    {
        var earlier = points
            .Where(kept => kept.TakenUtc < point.TakenUtc && kept.TakenUtc.Date != point.TakenUtc.Date)
            .ToList();

        return [.. earlier.Skip(Math.Max(0, earlier.Count + 1 - ProviderSizeStore.KeptPerProvider)), point];
    }

    private Dictionary<string, IReadOnlyList<SizePoint>> KeptLocked() =>
        _kept ??= new Dictionary<string, IReadOnlyList<SizePoint>>(store.Load(), StringComparer.Ordinal);
}
