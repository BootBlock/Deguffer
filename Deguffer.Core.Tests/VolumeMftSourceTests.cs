using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The source that reads a real volume, opened on a synthesised image that refuses every read a
/// disk with 4,096-byte sectors would refuse.
///
/// <para>Each layout here is one Windows produces. A disk with 4,096-byte sectors gets 1,024-byte
/// records by default, four to a sector, so the table's record boundaries are not its sector
/// boundaries; formatted with large records it gets 4,096. Where records are smaller than sectors,
/// the table ends part-way through a sector, as a real one can, so the reader also meets records no
/// whole-sector read can serve.</para>
/// </summary>
public class VolumeMftSourceTests
{
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Cache = 8;
    private const uint Nested = 9;
    private const uint Sibling = 10;

    private const long AcrossStride = 0x0000_1234_5678_9ABC;

    [Theory]
    [InlineData(512, 4096, 1024, null)]
    [InlineData(4096, 4096, 1024, null)]
    [InlineData(4096, 4096, 1024, 2L)]
    [InlineData(4096, 4096, 4096, null)]
    [InlineData(4096, 4096, 4096, 7L)]
    [InlineData(4096, 65536, 1024, null)]
    [InlineData(512, 512, 1024, 3L)]
    public void MeasuresATreeFromTheVolumeItIsOn(int bytesPerSector, int bytesPerCluster, int bytesPerRecord, long? gapAfterCluster)
    {
        using var source = Tree(bytesPerRecord).OpenVolume(bytesPerSector, bytesPerCluster, out var reason, gapAfterCluster);

        Assert.Equal(FallbackReason.None, reason);
        Assert.NotNull(source);
        Assert.Equal(23, source.RecordCount);
        Assert.True(MftVolumeIndexBuilder.TryBuild(source, TableTuning.Default, out var index));

        Assert.Equal(AcrossStride + 8192, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".config"])!.Value.Allocated);
    }

    /// <summary>
    /// Record 0 says where <c>$MFT</c>'s <c>$BITMAP</c> is, and the bitmap read from there marks the
    /// table's records in use and no others.
    /// </summary>
    [Theory]
    [InlineData(512, 4096, 1024)]
    [InlineData(4096, 4096, 1024)]
    [InlineData(4096, 65536, 4096)]
    public void ReadsTheBitmapRecordZeroLocates(int bytesPerSector, int bytesPerCluster, int bytesPerRecord)
    {
        using var source = Tree(bytesPerRecord).OpenVolume(bytesPerSector, bytesPerCluster, out _);

        Assert.NotNull(source?.Bitmap);

        var bitmap = MftBitmapReader.TryRead(source, source.RecordCount);
        Assert.NotNull(bitmap);

        long[] inUse = [0, MftRecord.RootRecordNumber, Users, Profile, Cache, Nested, Sibling, 12, 13, 14, 15, 20, 21, 22];
        Assert.Equal(inUse, Enumerable.Range(0, (int)source.RecordCount).Select(n => (long)n).Where(bitmap.IsInUse));
    }

    /// <summary>
    /// A pass plans each read before making it, so what the source says a read will bring has to be
    /// what the read brings, from every record, on every layout, read either way.
    /// </summary>
    [Theory]
    [InlineData(512, 4096, 1024, null)]
    [InlineData(4096, 4096, 1024, 2L)]
    [InlineData(4096, 4096, 4096, 7L)]
    [InlineData(512, 512, 1024, 3L)]
    public async Task ReadsWhatItPlannedToRead(int bytesPerSector, int bytesPerCluster, int bytesPerRecord, long? gapAfterCluster)
    {
        using var source = Tree(bytesPerRecord).OpenVolume(bytesPerSector, bytesPerCluster, out _, gapAfterCluster);
        using var buffer = new VolumeReadBuffer(8 * bytesPerRecord);

        Assert.NotNull(source);

        for (long first = 0; first <= source.RecordCount; first++)
        {
            var planned = source.BatchLength(first, 8);

            Assert.Equal(planned, source.ReadBatch(first, buffer.Span));
            Assert.Equal(planned, await source.ReadBatchAsync(first, buffer.Memory, CancellationToken.None));
        }
    }

    /// <summary>
    /// A volume is opened for overlapped reads, and the same handle serves the reads that are not
    /// overlapped. An ordinary file opened the same way shows both without administrator rights.
    /// </summary>
    [Fact]
    public async Task AHandleOpenedAsAVolumeServesEitherKindOfRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deguffer-{Guid.NewGuid():N}.bin");
        var bytes = Enumerable.Range(0, 3 * 4096).Select(i => (byte)(i * 7)).ToArray();
        await File.WriteAllBytesAsync(path, bytes);

        try
        {
            var handle = VolumeMftSource.OpenOverlapped(path);

            // Without overlapped I/O Windows serialises the reads on a handle, and every read in
            // flight but one waits behind it.
            Assert.True(handle.IsAsync);

            using var volume = new VolumeHandle(handle);
            using var first = new VolumeReadBuffer(4096);
            using var second = new VolumeReadBuffer(4096);
            using var third = new VolumeReadBuffer(4096);

            var reads = new[] { volume.ReadAsync(first.Memory, 0, CancellationToken.None), volume.ReadAsync(second.Memory, 4096, CancellationToken.None) };
            Assert.Equal(4096, volume.Read(third.Span, 8192));

            Assert.All(await Task.WhenAll(reads.Select(r => r.AsTask())), read => Assert.Equal(4096, read));
            Assert.True(bytes.AsSpan(0, 4096).SequenceEqual(first.Span));
            Assert.True(bytes.AsSpan(4096, 4096).SequenceEqual(second.Span));
            Assert.True(bytes.AsSpan(8192, 4096).SequenceEqual(third.Span));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A caller may start a batch at any record, and record 9 begins a quarter of the way into a
    /// sector. Each record served from there on has to be the record of that number.
    /// </summary>
    [Fact]
    public void ServesABatchStartingInsideASector()
    {
        const int BytesPerRecord = 1024;

        using var source = Tree(BytesPerRecord).OpenVolume(bytesPerSector: 4096, bytesPerCluster: 4096, out _);
        using var expected = Tree(BytesPerRecord).Build();
        using var actual = new VolumeReadBuffer(8 * BytesPerRecord);
        using var wanted = new VolumeReadBuffer(BytesPerRecord);

        Assert.NotNull(source);

        for (long next = 9; next < source.RecordCount;)
        {
            var read = source.ReadBatch(next, actual.Span);
            Assert.InRange(read, 1, 8);

            for (var i = 0; i < read; i++)
            {
                Assert.Equal(1, expected.ReadBatch(next + i, wanted.Span));
                Assert.True(wanted.Span.SequenceEqual(actual.Span.Slice(i * BytesPerRecord, BytesPerRecord)));
            }

            next += read;
        }
    }

    /// <summary>
    /// Opening a volume reads its boot sector and nothing past the volume's first 8 KiB, and its table
    /// is read only when asked for. NTFS flushes and purges what it has cached for every file on a
    /// volume at the first read through its handle that reaches past those 8 KiB (<see cref="IMftVolume"/>),
    /// so an open that read record 0 would make its caller wait for the flush. The table starts at
    /// 8 KiB here, so its first read is one that would pay.
    /// </summary>
    [Theory]
    [InlineData(512, 4096, 1024)]
    [InlineData(4096, 4096, 1024)]
    [InlineData(4096, 65536, 4096)]
    public void OpeningAVolumeReadsOnlyItsBootSectors(int bytesPerSector, int bytesPerCluster, int bytesPerRecord)
    {
        const long BootSectors = 8192;
        var volume = new RecordingVolume(Tree(bytesPerRecord).BuildVolume(bytesPerSector, bytesPerCluster));

        var opened = VolumeMftSource.TryOpen(volume, out var reason);

        Assert.Equal(FallbackReason.None, reason);
        Assert.NotNull(opened);
        Assert.NotEmpty(volume.Reads);
        Assert.All(volume.Reads, read => Assert.True(read.End <= BootSectors, $"The open read up to byte {read.End}."));

        using var source = opened.TryReadTable(out reason);

        Assert.Equal(FallbackReason.None, reason);
        Assert.NotNull(source);
        Assert.Contains(volume.Reads, read => read.End > BootSectors);
    }

    /// <summary>
    /// The handle has one owner at a time: the opened volume until its table is read, then the source.
    /// Closing the opened volume afterwards leaves the source reading, and its table is read once.
    /// </summary>
    [Fact]
    public void AnOpenedVolumeHandsItsHandleToTheSource()
    {
        var volume = new RecordingVolume(Tree(1024).BuildVolume(bytesPerSector: 4096, bytesPerCluster: 4096));
        var opened = VolumeMftSource.TryOpen(volume, out _)!;

        var source = opened.TryReadTable(out _)!;
        opened.Dispose();

        Assert.Equal(0, volume.Closed);
        Assert.Throws<InvalidOperationException>(() => opened.TryReadTable(out _));

        source.Dispose();
        Assert.Equal(1, volume.Closed);
    }

    /// <summary>A volume closed before its table is read closes the handle, and reads nothing more.</summary>
    [Fact]
    public void AnOpenedVolumeClosedUnreadClosesItsHandle()
    {
        var volume = new RecordingVolume(Tree(1024).BuildVolume(bytesPerSector: 4096, bytesPerCluster: 4096));
        var opened = VolumeMftSource.TryOpen(volume, out _)!;
        var reads = volume.Reads.Count;

        opened.Dispose();

        Assert.Equal(1, volume.Closed);
        Assert.Equal(reads, volume.Reads.Count);
        Assert.Throws<InvalidOperationException>(() => opened.TryReadTable(out _));
    }

    /// <summary>
    /// A table that cannot be found closes the handle on the way out, because nothing else holds it
    /// then, and says why. A leaked raw handle keeps the volume open until finalisation.
    /// </summary>
    [Fact]
    public void ATableThatCannotBeFoundClosesItsHandle()
    {
        var volume = new RecordingVolume(Tree(1024).BuildVolume(bytesPerSector: 4096, bytesPerCluster: 4096), refusedFrom: 8192);
        var opened = VolumeMftSource.TryOpen(volume, out _)!;

        Assert.Null(opened.TryReadTable(out var reason));
        opened.Dispose();

        Assert.Equal(FallbackReason.MasterFileTableIncomplete, reason);
        Assert.Equal(1, volume.Closed);
    }

    /// <summary>The same where the driver refuses the table's first read rather than serving nothing.</summary>
    [Fact]
    public void ATableTheDriverRefusesClosesItsHandle()
    {
        var volume = new RecordingVolume(
            Tree(1024).BuildVolume(bytesPerSector: 4096, bytesPerCluster: 4096), refusedFrom: 8192, refusalThrows: true);
        var opened = VolumeMftSource.TryOpen(volume, out _)!;

        Assert.Null(opened.TryReadTable(out var reason));
        opened.Dispose();

        Assert.Equal(FallbackReason.VolumeNotAddressable, reason);
        Assert.Equal(1, volume.Closed);
    }

    /// <summary>A volume whose boot sector is not NTFS hands its handle to nothing, so the open closes it.</summary>
    [Fact]
    public void AVolumeThatIsNotNtfsClosesItsHandle()
    {
        var volume = new RecordingVolume(new SectorStrictVolume(new byte[64 * 1024], bytesPerSector: 512));

        Assert.Null(VolumeMftSource.TryOpen(volume, out var reason));

        Assert.Equal(FallbackReason.NotNtfsVolume, reason);
        Assert.Equal(1, volume.Closed);
    }

    /// <summary>The same where the driver refuses the boot sector's read.</summary>
    [Fact]
    public void ABootSectorTheDriverRefusesClosesItsHandle()
    {
        var volume = new RecordingVolume(
            Tree(1024).BuildVolume(bytesPerSector: 4096, bytesPerCluster: 4096), refusedFrom: 0, refusalThrows: true);

        Assert.Null(VolumeMftSource.TryOpen(volume, out var reason));

        Assert.Equal(FallbackReason.VolumeNotAddressable, reason);
        Assert.Equal(1, volume.Closed);
    }

    /// <summary>
    /// The image has to refuse what the disk refuses, or every test above passes against a reader
    /// that reads one record at a time from wherever it likes.
    /// </summary>
    [Fact]
    public void TheImageRefusesAReadThatIsNotWholeSectors()
    {
        using var volume = Tree(1024).BuildVolume(bytesPerSector: 4096, bytesPerCluster: 4096);
        using var record = new VolumeReadBuffer(1024);
        using var sector = new VolumeReadBuffer(4096);

        Assert.Throws<IOException>(() => volume.Read(record.Span, 0));
        Assert.Throws<IOException>(() => volume.Read(sector.Span, 1024));
        Assert.Equal(4096, volume.Read(sector.Span, 0));
    }

    /// <summary>
    /// The file at 22 makes 23 records, which is not a whole number of 4,096-byte sectors of
    /// 1,024-byte records, so the last three are read where no batch can reach them.
    /// </summary>
    private static MftFixture Tree(int bytesPerRecord) => new MftFixture(bytesPerRecord)
        .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(Profile, Users, "testuser")
        .AddDirectory(Cache, Profile, ".npm-cache")
        .AddDirectory(Nested, Cache, "content-v2")
        .AddDirectory(Sibling, Profile, ".config")
        .AddFileWithSizeAcrossStrideBoundary(20, Cache, AcrossStride, logical: AcrossStride)
        .AddFile(21, Sibling, "c.json", allocated: 4096, logical: 100)
        .AddFile(22, Nested, "b.tgz", allocated: 8192, logical: 8000);

    /// <summary>A volume that notes where each read it serves starts and ends, and how often it was closed.</summary>
    /// <param name="refusedFrom">
    /// The offset from which a read is refused: it serves nothing, or throws as a driver's refusal
    /// does where <paramref name="refusalThrows"/>.
    /// </param>
    private sealed class RecordingVolume(IRawVolume inner, long refusedFrom = long.MaxValue, bool refusalThrows = false) : IRawVolume
    {
        public List<(long Offset, long End)> Reads { get; } = [];

        public int Closed { get; private set; }

        public int Read(Span<byte> destination, long offset)
        {
            Reads.Add((offset, offset + destination.Length));
            return IsRefused(offset) ? 0 : inner.Read(destination, offset);
        }

        public ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken ct)
        {
            Reads.Add((offset, offset + destination.Length));
            return IsRefused(offset) ? ValueTask.FromResult(0) : inner.ReadAsync(destination, offset, ct);
        }

        private bool IsRefused(long offset)
        {
            if (offset < refusedFrom)
            {
                return false;
            }

            return refusalThrows ? throw new IOException("The device is not ready.") : true;
        }

        public void Dispose()
        {
            Closed++;
            inner.Dispose();
        }
    }
}
