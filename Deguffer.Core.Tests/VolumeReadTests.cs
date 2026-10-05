using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The memory a raw volume read lands in, held to Microsoft's rules for an unbuffered read, which
/// ask for a buffer aligned to a sector.
///
/// <para>The reads themselves cannot run here: opening a volume needs administrator rights (§6.3),
/// and a disk with 512-byte sectors may accept a read that a 4,096-byte one refuses. So these hold
/// the addresses to the rules instead.</para>
/// </summary>
public class VolumeReadTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(1024 * 4096)]
    public void AlignsAReadBufferToTheLargestSector(int length)
    {
        using var buffer = new VolumeReadBuffer(length);

        Assert.Equal(0, Address(buffer.Span) % NtfsBootSector.MaximumBytesPerSector);
        Assert.Equal(length, buffer.Span.Length);
    }

    /// <summary>
    /// The batch is where nearly every byte of a table is read, so it is the read that most needs
    /// the alignment.
    /// </summary>
    [Fact]
    public void HandsASourceABatchAlignedToTheLargestSector()
    {
        using var source = new AddressRecordingSource();

        MftRecordStream.TryReadAll(source, count: 1, TableTuning.Default, (_, _, in _) => true, onProgress: null, CancellationToken.None);

        Assert.NotNull(source.Address);
        Assert.Equal(0, source.Address.Value % NtfsBootSector.MaximumBytesPerSector);
    }

    /// <summary>
    /// A list grown too large for its record is read from clusters outside the table, straight into
    /// the buffer handed over, so that buffer is held to the same rule.
    /// </summary>
    [Fact]
    public void HandsASourceAClusterReadAlignedToTheLargestSector()
    {
        using var source = new ClusterAddressRecordingSource(new MftFixture()
            .AddFileWithANonResidentAttributeList(
                20, MftRecord.RootRecordNumber, "fragmented.tgz", allocated: 8192, logical: 8000, extension: 21, listCluster: 500)
            .WithoutBitmap()
            .Build());

        MftRecordStream.TryReadAll(
            source, (int)source.RecordCount, TableTuning.Default, (_, _, in _) => true, onProgress: null, CancellationToken.None);

        Assert.NotNull(source.Address);
        Assert.Equal(0, source.Address.Value % NtfsBootSector.MaximumBytesPerSector);
    }

    private static long Address(Span<byte> span) =>
        Unsafe.ByteOffset(ref Unsafe.NullRef<byte>(), ref MemoryMarshal.GetReference(span));

    private sealed class AddressRecordingSource : IMftSource
    {
        public int BytesPerRecord => MftRecordBytes.BytesPerRecord;

        public long RecordCount => 1;

        public long? Address { get; private set; }

        public MftBitmapPlacement? Bitmap => null;

        public int BatchLength(long firstRecord, int capacity) => firstRecord < RecordCount ? 1 : 0;

        public int ReadBatch(long firstRecord, Span<byte> destination)
        {
            Address = VolumeReadTests.Address(destination);
            return 0;
        }

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            ValueTask.FromResult(ReadBatch(firstRecord, destination.Span));

        public int BytesPerCluster => 4096;

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => false;

        public void Dispose()
        {
        }
    }

    /// <summary>A fixture table that records where its cluster reads were asked to land.</summary>
    private sealed class ClusterAddressRecordingSource(IMftSource table) : IMftSource
    {
        public int BytesPerRecord => table.BytesPerRecord;

        public long RecordCount => table.RecordCount;

        public int BytesPerCluster => table.BytesPerCluster;

        public long? Address { get; private set; }

        public MftBitmapPlacement? Bitmap => table.Bitmap;

        public int BatchLength(long firstRecord, int capacity) => table.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination) => table.ReadBatch(firstRecord, destination);

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            table.ReadBatchAsync(firstRecord, destination, ct);

        public bool TryReadClusters(long firstCluster, Span<byte> destination)
        {
            Address = VolumeReadTests.Address(destination);
            return table.TryReadClusters(firstCluster, destination);
        }

        public void Dispose() => table.Dispose();
    }
}
