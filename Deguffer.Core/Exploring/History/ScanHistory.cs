namespace Deguffer.Core.Exploring.History;

/// <summary>What recording one scan produced.</summary>
/// <param name="Growth">
/// What grew since the newest earlier summary that could be read, or null where there was none.
/// </param>
/// <param name="Kept">The volume's kept summaries afterwards, oldest first, this scan's included where it was stored.</param>
/// <param name="Saved">Whether this scan's summary was stored, so the next scan can be compared with it.</param>
public sealed record ScanRecord(ScanGrowth? Growth, IReadOnlyList<KeptSummary> Kept, bool Saved);

/// <summary>
/// The kept summaries of every volume, and the one place a finished scan is recorded and compared.
///
/// <para>This orchestrates: <see cref="ScanSummaries"/> takes a summary, <see cref="ScanGrowth"/>
/// compares two, and <see cref="ScanHistoryStore"/> reads and writes them (G2). What it adds is the
/// order those happen in, and a memory of each volume's list for the life of the app, so a page
/// asking again does not read every file again (G4).</para>
///
/// <para>Shared by every caller, as one instance, because two would disagree about what is kept the
/// moment either removed anything.</para>
/// </summary>
public sealed class ScanHistory(ScanHistoryStore store)
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, IReadOnlyList<KeptSummary>> _byVolume = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised after anything kept is removed, so a page showing a comparison with it can stop. Raised
    /// on the thread that removed it.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Compare <paramref name="scan"/>, a finished scan of the whole of <paramref name="volume"/>,
    /// with the newest earlier summary of it, then store this one.
    ///
    /// <para>Compared before it is stored, so a scan is never compared with itself. The newest earlier
    /// summary that can be read is the one used, because a damaged one is nothing to compare with
    /// (<see cref="ScanHistoryStore"/>), and the one before it still describes the volume.</para>
    /// </summary>
    public ScanRecord Record(ExploreScan scan, string volume, VolumeSpace space, DateTime takenUtc)
    {
        ArgumentNullException.ThrowIfNull(scan);

        var now = ScanSummaries.Take(scan, volume, space, takenUtc);

        lock (_gate)
        {
            var kept = KeptLocked(volume);
            ScanGrowth? growth = null;

            for (var i = kept.Count - 1; i >= 0 && growth is null; i--)
            {
                if (store.Read(kept[i]) is { } earlier)
                {
                    growth = ScanGrowth.Between(earlier, now, scan.Tree);
                }
            }

            var saved = store.Save(now);
            _byVolume.Remove(volume);

            return new ScanRecord(growth, KeptLocked(volume), saved);
        }
    }

    /// <summary>The summaries kept for <paramref name="volume"/>, oldest first.</summary>
    public IReadOnlyList<KeptSummary> Kept(string volume)
    {
        lock (_gate)
        {
            return KeptLocked(volume);
        }
    }

    /// <summary>Every summary kept for any volume, newest first. Read afresh, for Settings.</summary>
    public IReadOnlyList<KeptSummary> All()
    {
        lock (_gate)
        {
            return store.List();
        }
    }

    /// <summary>Remove <paramref name="kept"/>. Returns whether it is gone.</summary>
    public bool Remove(KeptSummary kept)
    {
        bool removed;

        lock (_gate)
        {
            removed = store.Remove(kept);
            _byVolume.Remove(kept.Volume);
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return removed;
    }

    /// <summary>Remove every kept summary. Returns whether all of them are gone.</summary>
    public bool RemoveAll()
    {
        bool removed;

        lock (_gate)
        {
            removed = store.RemoveAll();
            _byVolume.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return removed;
    }

    private IReadOnlyList<KeptSummary> KeptLocked(string volume)
    {
        if (!_byVolume.TryGetValue(volume, out var kept))
        {
            kept = store.List(volume);
            _byVolume[volume] = kept;
        }

        return kept;
    }
}
