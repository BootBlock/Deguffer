namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One built index per volume, for the life of a scan.
///
/// G4/G5: building the index is the whole cost of the fast path, and every provider asks about
/// paths on the same volume. Building it per query would make the MFT route slower than the walk
/// it replaces. Failures are remembered too — an unelevated process would otherwise attempt, and
/// lose, a volume open for every path measured.
/// </summary>
public sealed class MftVolumeIndexCache(IMftSourceFactory factory)
{
    private readonly Dictionary<char, Lazy<Entry>> _byVolume = [];
    private readonly Lock _gate = new();

    private readonly record struct Entry(MftVolumeIndex? Index, FallbackReason Reason);

    /// <summary>
    /// The index for <paramref name="driveLetter"/>, or null with the reason the fast path is
    /// unavailable for it.
    /// </summary>
    public MftVolumeIndex? Get(char driveLetter, out FallbackReason reason, CancellationToken ct = default)
    {
        var key = char.ToUpperInvariant(driveLetter);
        Lazy<Entry> pending;

        // The lock covers only the dictionary; the Lazy serialises builds of the *same* volume,
        // which is the duplication worth preventing. Holding the lock across the build instead
        // would make a scan of D: wait for an unrelated multi-second build of C:.
        lock (_gate)
        {
            if (!_byVolume.TryGetValue(key, out pending!))
            {
                pending = new Lazy<Entry>(() => Build(key, ct), LazyThreadSafetyMode.ExecutionAndPublication);
                _byVolume[key] = pending;
            }
        }

        var entry = pending.Value;
        reason = entry.Reason;

        return entry.Index;
    }

    private Entry Build(char driveLetter, CancellationToken ct)
    {
        var source = factory.TryOpen(driveLetter, out var reason);
        if (source is null)
        {
            return new Entry(null, reason);
        }

        using (source)
        {
            try
            {
                return MftVolumeIndexBuilder.TryBuild(source, out var index, ct)
                    ? new Entry(index, FallbackReason.None)
                    : new Entry(null, FallbackReason.MasterFileTableUnreadable);
            }
            catch (IOException)
            {
                // The volume went away mid-scan, or the driver refused a read. Both mean this
                // volume takes the slow route; neither should take the preview down.
                return new Entry(null, FallbackReason.MasterFileTableUnreadable);
            }
        }
    }

    /// <summary>Drop every index, so the next scan sees the machine as it is now.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _byVolume.Clear();
        }
    }
}
