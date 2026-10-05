using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The update sequence fixup on its own, where the array's shape is the thing under test. Its
/// effect on a measured size is tested through the index, in <see cref="MftVolumeIndexTests"/>.
/// </summary>
public class UpdateSequenceArrayTests
{
    private const int ArrayOffset = 0x30;

    /// <summary>
    /// A disk with 4,096-byte sectors stamps its 4,096-byte records every 512 bytes all the same,
    /// so the array holds nine entries. A stride taken from the sector size refuses every one.
    /// </summary>
    [Fact]
    public void RestoresEveryStrideOfAFourKilobyteRecord()
    {
        var record = Record(bytesPerRecord: 4096);

        Assert.True(UpdateSequenceArray.TryApply(record, ArrayOffset, count: 9));
    }

    /// <summary>
    /// The stamp in the last stride is the one a reader that stops early never checks, and never
    /// restores either.
    /// </summary>
    [Fact]
    public void RefusesAFourKilobyteRecordTornInItsLastStride()
    {
        var record = Record(bytesPerRecord: 4096);
        record[4095] ^= 0xFF;

        Assert.False(UpdateSequenceArray.TryApply(record, ArrayOffset, count: 9));
    }

    /// <summary>
    /// An array shorter than the record restores the strides it covers and leaves a stamp in place
    /// of two real bytes in each of the rest. Accepting it reports those stamps as data.
    /// </summary>
    [Theory]
    [InlineData(1024, 2)]
    [InlineData(4096, 2)]
    [InlineData(4096, 8)]
    public void RefusesAnArrayThatDoesNotCoverTheWholeRecord(int bytesPerRecord, int count)
    {
        var record = Record(bytesPerRecord);

        Assert.False(UpdateSequenceArray.TryApply(record, ArrayOffset, count));
    }

    [Fact]
    public void RefusesAnArrayLongerThanTheRecord()
    {
        var record = Record(bytesPerRecord: 1024);

        Assert.False(UpdateSequenceArray.TryApply(record, ArrayOffset, count: 9));
    }

    private static byte[] Record(int bytesPerRecord) =>
        MftRecordBytes.SelfRecord([new DataRun(786_432, 64)], dataSize: 65_536, bytesPerRecord: bytesPerRecord);
}
