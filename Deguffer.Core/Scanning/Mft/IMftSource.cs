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
    ///
    /// <para>A volume source reads straight into <paramref name="destination"/>, so a caller passes
    /// a <see cref="VolumeReadBuffer"/> rather than a managed array.</para>
    /// </summary>
    int ReadBatch(long firstRecord, Span<byte> destination);

    /// <summary>The volume's allocation unit, which a non-resident attribute's runs count in.</summary>
    int BytesPerCluster { get; }

    /// <summary>
    /// Fill <paramref name="destination"/>, a whole number of clusters long, from the volume's
    /// clusters starting at <paramref name="firstCluster"/>. Returns false unless every byte was
    /// read.
    ///
    /// <para>Needed for the one thing in a table that is clusters rather than records: an
    /// <c>$ATTRIBUTE_LIST</c> grown too large to stay inside its record, which NTFS then keeps
    /// outside the table altogether.</para>
    ///
    /// <para>A volume source reads straight into <paramref name="destination"/>, so, as for
    /// <see cref="ReadBatch"/>, a caller passes a <see cref="VolumeReadBuffer"/>.</para>
    /// </summary>
    bool TryReadClusters(long firstCluster, Span<byte> destination);
}
