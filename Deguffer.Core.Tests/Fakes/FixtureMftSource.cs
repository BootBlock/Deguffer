using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Serves records built by <see cref="MftFixture"/>, and can refuse to serve them from a chosen
/// point on — standing in for a bad sector or a run list the reader could not follow.
/// </summary>
public sealed class FixtureMftSource(
    IReadOnlyList<byte[]> records,
    int bytesPerSector,
    int bytesPerRecord,
    long unreadableFrom) : IMftSource
{
    public int BytesPerSector => bytesPerSector;

    public int BytesPerRecord => bytesPerRecord;

    public long RecordCount => records.Count;

    public int ReadBatch(long firstRecord, Span<byte> destination)
    {
        if (firstRecord >= unreadableFrom)
        {
            return 0;
        }

        var capacity = destination.Length / bytesPerRecord;
        var available = (int)Math.Min(capacity, Math.Min(records.Count, unreadableFrom) - firstRecord);

        for (var i = 0; i < available; i++)
        {
            records[(int)firstRecord + i].CopyTo(destination[(i * bytesPerRecord)..]);
        }

        return Math.Max(0, available);
    }

    public void Dispose()
    {
    }
}
