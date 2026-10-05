using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The on-disk geometry the MFT reader depends on before it can read a single record.
///
/// These values multiply into every later byte offset, so a mistake here does not fail visibly —
/// it reads the right number of bytes from the wrong place and produces records that parse cleanly
/// and describe nothing.
/// </summary>
public class NtfsGeometryTests
{
    private static byte[] BootSector(
        ushort bytesPerSector = 512,
        byte sectorsPerCluster = 8,
        long mftStart = 786_432,
        sbyte clustersPerRecord = -10) =>
        NtfsBootSectorBytes.Build(bytesPerSector, sectorsPerCluster, mftStart, clustersPerRecord);

    [Fact]
    public void ReadsAConventionalVolumeLayout()
    {
        Assert.True(NtfsBootSector.TryParse(BootSector(), out var geometry));

        Assert.Equal(512, geometry.BytesPerSector);
        Assert.Equal(4096, geometry.BytesPerCluster);
        Assert.Equal(786_432, geometry.MftStartCluster);
        Assert.Equal(1024, geometry.BytesPerFileRecord);
    }

    /// <summary>
    /// A disk with native 4,096-byte sectors, laid out as Windows formats one: 1,024-byte records,
    /// four to a sector. Refusing a record smaller than a sector refused every such volume, and its
    /// record 0 is stamped every 512 bytes like any other, so the table's extents read from it.
    /// </summary>
    [Fact]
    public void ReadsANativeFourKilobyteSectorLayout()
    {
        Assert.True(NtfsBootSector.TryParse(
            BootSector(bytesPerSector: 4096, sectorsPerCluster: 1, clustersPerRecord: -10),
            out var geometry));

        Assert.Equal(4096, geometry.BytesPerSector);
        Assert.Equal(4096, geometry.BytesPerCluster);
        Assert.Equal(1024, geometry.BytesPerFileRecord);

        var record = MftRecordBytes.SelfRecord(
            [new DataRun(786_432, 64), new DataRun(900_000, 32)],
            dataSize: 393_216,
            bytesPerRecord: geometry.BytesPerFileRecord);

        Assert.True(MftExtentMapReader.TryRead(record, geometry.BytesPerCluster, MftExtentMapTests.NoClusters, out var map));
        Assert.Equal([new DataRun(786_432, 64), new DataRun(900_000, 32)], map.Runs);
    }

    /// <summary>
    /// A cluster count past what NTFS formats: 2^20 sectors of 4,096 bytes and 2^23 of 512 wrap the
    /// cluster size to zero, and 0xA0 asks for a shift C# would take modulo 32. Each has to refuse
    /// the volume rather than throw from the open or read it with the wrong cluster size.
    /// </summary>
    [Theory]
    [InlineData(4096, 0xEC)]
    [InlineData(512, 0xE9)]
    [InlineData(512, 0xA0)]
    [InlineData(512, 0x81)]
    [InlineData(4096, 0xF6)]
    public void RejectsAClusterLargerThanNtfsFormats(ushort bytesPerSector, byte sectorsPerCluster) =>
        Assert.False(NtfsBootSector.TryParse(
            BootSector(bytesPerSector, sectorsPerCluster, clustersPerRecord: -12),
            out _));

    /// <summary>The largest cluster Windows formats, 2 MiB, encoded as the negative exponent form.</summary>
    [Theory]
    [InlineData(4096, 0xF7)]
    [InlineData(512, 0xF4)]
    public void ReadsTheLargestClusterNtfsFormats(ushort bytesPerSector, byte sectorsPerCluster)
    {
        Assert.True(NtfsBootSector.TryParse(
            BootSector(bytesPerSector, sectorsPerCluster, clustersPerRecord: -12),
            out var geometry));

        Assert.Equal(NtfsBootSector.MaximumBytesPerCluster, geometry.BytesPerCluster);
    }

    /// <summary>
    /// A record smaller than one fixup stride has no room for the array that protects it, so not
    /// one record of the table could be read. Refused here, the volume reports itself as not NTFS
    /// rather than as a table that could not be read.
    /// </summary>
    [Fact]
    public void RejectsARecordSmallerThanOneFixupStride() =>
        Assert.False(NtfsBootSector.TryParse(
            BootSector(bytesPerSector: 256, sectorsPerCluster: 1, clustersPerRecord: 1),
            out _));

    /// <summary>
    /// The boot sector is read before the sector size is known, so that one read has to be a whole
    /// number of sectors on every volume the parse accepts.
    /// </summary>
    [Fact]
    public void ReadsTheBootSectorInWholeSectorsOfEverySizeAVolumeMayDeclare()
    {
        var accepted = Enumerable.Range(0, 16)
            .Select(power => (ushort)(1 << power))
            .Where(size => NtfsBootSector.TryParse(
                BootSector(bytesPerSector: size, sectorsPerCluster: 1, clustersPerRecord: -12),
                out _))
            .ToList();

        Assert.Contains((ushort)4096, accepted);
        Assert.All(accepted, size => Assert.Equal(0, VolumeMftSource.BootReadBytes % size));
    }

    /// <summary>
    /// The record size has two encodings, and only the negative one appears on modern volumes.
    /// The positive form is legal, and reading it as a byte count would give 2 rather than 8192.
    /// </summary>
    [Fact]
    public void ReadsARecordSizeGivenAsAClusterCount()
    {
        Assert.True(NtfsBootSector.TryParse(BootSector(clustersPerRecord: 2), out var geometry));

        Assert.Equal(8192, geometry.BytesPerFileRecord);
    }

    [Fact]
    public void RejectsAVolumeThatIsNotNtfs()
    {
        var sector = BootSector();
        "FAT32   "u8.CopyTo(sector.AsSpan(3));

        Assert.False(NtfsBootSector.TryParse(sector, out _));
    }

    /// <summary>
    /// Nonsense geometry has to be rejected rather than propagated: a zero or non-power-of-two
    /// sector size would turn every subsequent offset into garbage that still looks like a number.
    /// </summary>
    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)500)]
    [InlineData((ushort)8192)]
    public void RejectsAnImpossibleSectorSize(ushort bytesPerSector) =>
        Assert.False(NtfsBootSector.TryParse(BootSector(bytesPerSector: bytesPerSector), out _));

    [Fact]
    public void RejectsAVolumeClaimingItsTableStartsAtClusterZero() =>
        Assert.False(NtfsBootSector.TryParse(BootSector(mftStart: 0), out _));

    /// <summary>
    /// The start cluster is multiplied by the cluster size to find record 0. One past the last
    /// cluster a long can address wraps that offset negative, and the read then throws rather than
    /// letting the volume fall back to the walk. The last addressable cluster is still a volume.
    /// </summary>
    [Fact]
    public void RejectsATableStartPastAnyAddressableByte()
    {
        const long LastAddressableCluster = long.MaxValue / 4096;

        Assert.False(NtfsBootSector.TryParse(BootSector(mftStart: LastAddressableCluster + 1), out _));
        Assert.True(NtfsBootSector.TryParse(BootSector(mftStart: LastAddressableCluster), out _));
    }

    [Fact]
    public void RejectsATruncatedSector() =>
        Assert.False(NtfsBootSector.TryParse(new byte[64], out _));
}

/// <summary>
/// Data runs — the delta-encoded cluster map that says where the MFT physically lives. The table
/// is not contiguous on any volume whose MFT has grown, which is most of them.
/// </summary>
public class DataRunTests
{
    [Fact]
    public void ReadsASingleRun()
    {
        // 0x21: a one-byte length and a two-byte offset. Five clusters at cluster 256.
        var runs = DataRuns.Parse([0x21, 0x05, 0x00, 0x01, 0x00]);

        Assert.Equal([new DataRun(256, 5)], runs);
    }

    /// <summary>
    /// The second and later offsets are signed deltas from the previous run's start, not absolute
    /// clusters. Reading them as absolute happens to work for the first run and then silently
    /// points every subsequent read at the wrong part of the disk.
    /// </summary>
    [Fact]
    public void TreatsLaterOffsetsAsSignedDeltas()
    {
        var runs = DataRuns.Parse(
        [
            0x21, 0x05, 0x00, 0x01, // 5 clusters at 256
            0x21, 0x03, 0x00, 0xFF, // delta -256 → 3 clusters at 0
            0x21, 0x02, 0x10, 0x00, // delta +16 → 2 clusters at 16
            0x00,
        ]);

        Assert.Equal([new DataRun(256, 5), new DataRun(0, 3), new DataRun(16, 2)], runs);
    }

    /// <summary>
    /// A sparse run has a length but no location. It must still be returned: virtual cluster
    /// numbers are positional, so dropping it would shift every later run down by its length and
    /// point subsequent reads at the wrong part of the disk.
    /// </summary>
    [Fact]
    public void KeepsSparseRunsSoLaterRunsStayAtTheRightPosition()
    {
        var runs = DataRuns.Parse([0x01, 0x08, 0x21, 0x02, 0x00, 0x01, 0x00]);

        Assert.Equal([new DataRun(0, 8, IsSparse: true), new DataRun(256, 2)], runs);
    }

    [Fact]
    public void StopsAtTheTerminator()
    {
        var runs = DataRuns.Parse([0x21, 0x05, 0x00, 0x01, 0x00, 0x21, 0xFF, 0xFF, 0xFF]);

        Assert.Single(runs);
    }

    [Fact]
    public void StopsRatherThanReadingPastTheEnd()
    {
        var runs = DataRuns.Parse([0x41, 0x05, 0x00]);

        Assert.Empty(runs);
    }
}

/// <summary>
/// Translating a position in the MFT stream to a position on the disk. Reading across an extent
/// boundary would splice unrelated regions into what looks like a run of consecutive records.
/// </summary>
public class MftExtentMapTests
{
    /// <summary>
    /// For a record 0 with no list to follow, so nothing beyond it is read. A reader that went
    /// looking would be reading clusters no test placed anything in.
    /// </summary>
    internal static readonly ClusterReader NoClusters = static (_, _) => false;

    private static readonly MftExtentMap Map =
        new(DataSize: 8 * 1024, Runs: [new DataRun(100, 5), new DataRun(200, 3)]);

    [Theory]
    [InlineData(0, 100, 5)]
    [InlineData(3, 103, 2)]
    [InlineData(5, 200, 3)]
    [InlineData(7, 202, 1)]
    public void TranslatesWithinAndAcrossExtents(long virtualCluster, long expectedCluster, long expectedRemaining)
    {
        Assert.True(Map.TryTranslate(virtualCluster, out var physical, out var contiguous));

        Assert.Equal(expectedCluster, physical);

        // The contiguous count is what stops a batch straddling the gap between extents.
        Assert.Equal(expectedRemaining, contiguous);
    }

    /// <summary>
    /// A 2,048-byte record on a volume of 512-byte clusters, whose four clusters lie in three
    /// extents. Every one of them has to be found, in order: reading the tail as though it were
    /// contiguous with the second extent would splice in a cluster of something else.
    /// </summary>
    [Fact]
    public void ReadsARecordSpreadAcrossThreeExtents()
    {
        const int ClusterBytes = 512;

        var map = new MftExtentMap(DataSize: 4 * 2048, Runs: [new DataRun(100, 1), new DataRun(200, 1), new DataRun(300, 6)]);
        var clusters = new Dictionary<long, byte[]>();

        foreach (var cluster in new long[] { 100, 200, 201, 300, 301, 302, 303, 304, 305 })
        {
            clusters[cluster] = Enumerable.Repeat((byte)(cluster % 251), ClusterBytes).ToArray();
        }

        var record = new byte[2048];
        Assert.True(map.TryReadRecord(0, ClusterBytes, (first, destination) =>
        {
            for (var i = 0; i < destination.Length / ClusterBytes; i++)
            {
                if (!clusters.TryGetValue(first + i, out var bytes))
                {
                    return false;
                }

                bytes.CopyTo(destination[(i * ClusterBytes)..]);
            }

            return true;
        }, record));

        Assert.Equal(
            new long[] { 100, 200, 300, 301 }.SelectMany(c => clusters[c]),
            record);
    }

    /// <summary>
    /// The extents run on past the table's end, as an allocation can. A record there is not one of
    /// the table's records, whatever the clusters hold.
    /// </summary>
    [Fact]
    public void RefusesToReadARecordPastTheEndOfTheTable()
    {
        var map = new MftExtentMap(DataSize: 4 * 1024, Runs: [new DataRun(100, 8)]);
        ClusterReader everything = static (_, _) => true;

        Assert.True(map.TryReadRecord(3, bytesPerCluster: 1024, everything, new byte[1024]));
        Assert.False(map.TryReadRecord(4, bytesPerCluster: 1024, everything, new byte[1024]));
    }

    [Fact]
    public void RefusesAClusterPastTheEndOfTheTable() =>
        Assert.False(Map.TryTranslate(8, out _, out _));

    /// <summary>
    /// The bug a dropped sparse run causes, stated directly: a sparse region occupies virtual
    /// clusters, so the run after it starts at VCN 9, not VCN 5. Getting this wrong reads real
    /// records from the wrong offset — they mostly fail the signature check and vanish, and the
    /// ones that survive attach real sizes to the wrong directories.
    /// </summary>
    [Fact]
    public void CountsSparseRunsWhenTranslating()
    {
        var map = new MftExtentMap(
            DataSize: 12 * 1024,
            Runs: [new DataRun(100, 5), new DataRun(0, 4, IsSparse: true), new DataRun(200, 3)]);

        Assert.True(map.TryTranslate(9, out var physical, out var contiguous));
        Assert.Equal(200, physical);
        Assert.Equal(3, contiguous);

        // Inside the hole there is nothing to read, and saying otherwise would hand back the next
        // run's clusters under the wrong virtual address.
        Assert.False(map.TryTranslate(6, out _, out _));
    }

    [Fact]
    public void ReadsTheTablesOwnExtentsFromRecordZero()
    {
        var record = MftRecordBytes.SelfRecord([new DataRun(786_432, 64), new DataRun(900_000, 32)], dataSize: 98_304);

        Assert.True(MftExtentMapReader.TryRead(record, bytesPerCluster: 4096, NoClusters, out var map));

        Assert.Equal(98_304, map.DataSize);
        Assert.Equal([new DataRun(786_432, 64), new DataRun(900_000, 32)], map.Runs);
    }

    /// <summary>
    /// Record 0 can carry an $ATTRIBUTE_LIST that names only record 0 itself, where the attributes
    /// still fit. There is nothing to follow, and the table is read as though there were no list.
    /// </summary>
    [Fact]
    public void ReadsATableWhoseListNamesOnlyRecordZero()
    {
        var record = MftRecordBytes.SelfRecord([new DataRun(786_432, 64)], dataSize: 65_536, withAttributeList: true);

        Assert.True(MftExtentMapReader.TryRead(record, bytesPerCluster: 4096, NoClusters, out var map));
        Assert.Equal([new DataRun(786_432, 64)], map.Runs);
    }

    /// <summary>
    /// The table is one byte longer than its runs reach. A map that ends short indexes part of the
    /// volume and reports short sizes for the rest, so it is refused like any other damage.
    /// </summary>
    [Fact]
    public void RefusesRunsThatEndShortOfTheTable()
    {
        var past = MftRecordBytes.SelfRecord([new DataRun(786_432, 16)], dataSize: (16 * 4096) + 1);
        var exact = MftRecordBytes.SelfRecord([new DataRun(786_432, 16)], dataSize: 16 * 4096);

        Assert.False(MftExtentMapReader.TryRead(past, bytesPerCluster: 4096, NoClusters, out _));
        Assert.True(MftExtentMapReader.TryRead(exact, bytesPerCluster: 4096, NoClusters, out _));
    }

    [Fact]
    public void RefusesARecordThatIsNotARecord() =>
        Assert.False(MftExtentMapReader.TryRead(new byte[1024], bytesPerCluster: 4096, NoClusters, out _));

    /// <summary>
    /// Record 0 is read while the volume is being opened, so a throw here would escape before the
    /// volume could fall back to the walk. A corrupt length has to refuse like any other damage.
    /// </summary>
    [Fact]
    public void RefusesRecordZeroWhenAnAttributeOverrunsIt()
    {
        var record = MftRecordBytes.SelfRecord([new DataRun(786_432, 64)], dataSize: 65_536);
        MftRecordBytes.DeclareFirstAttributeLength(record, MftRecordBytes.LengthJustUnderIntMax);

        Assert.False(MftExtentMapReader.TryRead(record, bytesPerCluster: 4096, NoClusters, out _));
    }

    /// <summary>
    /// A run list can name a cluster up to 2^63, and the reader multiplies each cluster by the
    /// cluster size. A run ending past the last addressable byte wraps that offset negative, and
    /// the read throws out of the index build rather than refusing it. A run ending exactly at the
    /// limit is still a run.
    /// </summary>
    [Fact]
    public void RefusesARunEndingPastAnyAddressableByte()
    {
        const long LastAddressableCluster = long.MaxValue / 4096;

        var past = MftRecordBytes.SelfRecord([new DataRun(LastAddressableCluster - 63, 64)], dataSize: 65_536);
        var atTheLimit = MftRecordBytes.SelfRecord([new DataRun(LastAddressableCluster - 64, 64)], dataSize: 65_536);

        Assert.False(MftExtentMapReader.TryRead(past, bytesPerCluster: 4096, NoClusters, out _));
        Assert.True(MftExtentMapReader.TryRead(atTheLimit, bytesPerCluster: 4096, NoClusters, out _));
    }
}
