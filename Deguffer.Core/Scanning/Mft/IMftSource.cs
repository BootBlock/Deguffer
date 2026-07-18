namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Supplies raw MFT records, in batches, to whatever wants to parse them.
///
/// This is the seam that keeps the fast path testable. Opening a volume handle needs administrator
/// rights (§6.3), so the index, the parser and the aggregation all sit above this interface and are
/// exercised against synthesised records; only <see cref="VolumeMftSource"/> touches a real disk.
///
/// Batched rather than per-record because the MFT is millions of 1 KB records: a syscall each would
/// cost more than the directory walk this exists to replace (G4).
/// </summary>
public interface IMftSource : IDisposable
{
    /// <summary>Needed to undo the per-sector update sequence fixup.</summary>
    int BytesPerSector { get; }

    int BytesPerRecord { get; }

    /// <summary>How many records the table holds, from <c>$MFT</c>'s own data size.</summary>
    long RecordCount { get; }

    /// <summary>
    /// Fill <paramref name="destination"/> with consecutive records from
    /// <paramref name="firstRecord"/>, returning how many were written.
    ///
    /// May return fewer than the buffer holds — the MFT is not necessarily contiguous, and a batch
    /// stops at an extent boundary rather than reading across the gap. Returns zero at the end of
    /// the table, or where a region cannot be read at all.
    /// </summary>
    int ReadBatch(long firstRecord, Span<byte> destination);
}
