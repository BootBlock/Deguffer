using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// A volume's file table that counts what it hands out.
///
/// <para>Counted here rather than worked out from the record count, because a read can stop short:
/// at a region that cannot be read, or where the index abandons the volume. A rate worked out from
/// the size of the table would then report bytes the run never read.</para>
/// </summary>
internal sealed class CountingMftSource(IMftSource inner) : IMftSource
{
    public int BytesPerRecord => inner.BytesPerRecord;

    public long RecordCount => inner.RecordCount;

    public long RecordsRead { get; private set; }

    public long BytesRead => RecordsRead * BytesPerRecord;

    public int ReadBatch(long firstRecord, Span<byte> destination)
    {
        var read = inner.ReadBatch(firstRecord, destination);
        RecordsRead += Math.Max(read, 0);
        return read;
    }

    public void Dispose() => inner.Dispose();
}
