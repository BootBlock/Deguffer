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
        var volume = Tree(bytesPerRecord).BuildVolume(bytesPerSector, bytesPerCluster, gapAfterCluster);

        using var source = VolumeMftSource.TryOpen(volume, out var reason);

        Assert.Equal(FallbackReason.None, reason);
        Assert.NotNull(source);
        Assert.Equal(23, source.RecordCount);
        Assert.True(MftVolumeIndexBuilder.TryBuild(source, TableTuning.Default, out var index));

        Assert.Equal(AcrossStride + 8192, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".config"])!.Value.Allocated);
    }

    /// <summary>
    /// A caller may start a batch at any record, and record 9 begins a quarter of the way into a
    /// sector. Each record served from there on has to be the record of that number.
    /// </summary>
    [Fact]
    public void ServesABatchStartingInsideASector()
    {
        const int BytesPerRecord = 1024;

        using var source = VolumeMftSource.TryOpen(
            Tree(BytesPerRecord).BuildVolume(bytesPerSector: 4096, bytesPerCluster: 4096), out _);
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
}
