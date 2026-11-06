using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A slow open is traced to its step only where every step is timed apart, and each read is counted
/// against the step that made it. A read counted against the wrong step would send the search for a
/// slow open to the wrong place: the extension records rather than record 0, say.
///
/// <para>The fragmented table is <see cref="MftExtentMapReaderTests"/>' own: 32 records in four
/// extents, described by pieces in record 0, record 16 and record 19, with 1,024-byte records in
/// 512-byte clusters.</para>
/// </summary>
public sealed class MftOpenTimingsTests
{
    private const long DataSize = 32 * FragmentedMftVolume.BytesPerRecord;

    private static readonly DataRun[] Layout =
        [new DataRun(1_000, 33), new DataRun(2_000, 3), new DataRun(3_000, 8), new DataRun(4_000, 20)];

    private static readonly MftPiece[] Pieces =
    [
        new MftPiece(Holder: 0, LowestVcn: 0, [Layout[0], Layout[1]]),
        new MftPiece(Holder: 16, LowestVcn: 36, [Layout[2]]),
        new MftPiece(Holder: 19, LowestVcn: 44, [Layout[3]]),
    ];

    /// <summary>
    /// An open of a volume image reports every step after the handle once and in order, with the
    /// boot sector's one read and record 0's one read against their own steps. A table in one piece
    /// has no list, so nothing after record 0 reads at all.
    /// </summary>
    [Theory]
    [InlineData(512, 4096, 1024)]
    [InlineData(4096, 65536, 4096)]
    public void AnOpenReportsEachStepAfterTheHandleOnceAndInOrder(int bytesPerSector, int bytesPerCluster, int bytesPerRecord)
    {
        var timings = new MftOpenTimings();
        timings.Start();

        using var source = VolumeMftSource.TryOpen(
            new MftFixture(bytesPerRecord).BuildVolume(bytesPerSector, bytesPerCluster), out _, timings);

        Assert.NotNull(source);
        Assert.Equal(
            [
                (MftOpenStep.BootSector, 1),
                (MftOpenStep.FirstRecord, 1),
                (MftOpenStep.AttributeList, 0),
                (MftOpenStep.ExtensionRecords, 0),
                (MftOpenStep.BitmapPlacement, 0),
            ],
            timings.Steps.Select(step => (step.Step, step.Reads)));
    }

    /// <summary>
    /// Record 16 straddles the first two extents, so it takes a read from each, and record 19 lies in
    /// one: three reads, all against the extension records. The list kept
    /// outside record 0 is one cluster, read against the list.
    /// </summary>
    [Fact]
    public void CountsEachReadAgainstTheStepThatMadeIt()
    {
        var volume = new FragmentedMftVolume(Layout, DataSize, Pieces, listOutside: true);
        var timings = new MftOpenTimings();
        timings.Start();

        Assert.True(TryRead(volume, timings));

        Assert.Equal(
            [
                (MftOpenStep.AttributeList, 1),
                (MftOpenStep.ExtensionRecords, 3),
                (MftOpenStep.BitmapPlacement, 0),
            ],
            timings.Steps.Select(step => (step.Step, step.Reads)));
    }

    /// <summary>A step the open stopped before is absent, not zero, so a failed open is not read as a fast one.</summary>
    [Fact]
    public void AStepTheOpenStoppedBeforeIsAbsent()
    {
        var volume = new FragmentedMftVolume(Layout, DataSize, Pieces);
        volume.Lose(2_000);
        var timings = new MftOpenTimings();
        timings.Start();

        Assert.False(TryRead(volume, timings));

        Assert.Equal([MftOpenStep.AttributeList], timings.Steps.Select(step => step.Step));
        Assert.Null(timings.Of(MftOpenStep.ExtensionRecords));
    }

    /// <summary>Each step runs from where the one before it ended, so the steps add up to the open.</summary>
    [Fact]
    public void EachStepIsTimedFromTheEndOfTheOneBefore()
    {
        var clock = new ManualTimeProvider();
        var timings = new MftOpenTimings(clock);

        timings.Start();
        clock.Advance(TimeSpan.FromMilliseconds(2));
        timings.End(MftOpenStep.VolumeHandle);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        timings.End(MftOpenStep.FirstRecord);

        Assert.Equal(TimeSpan.FromMilliseconds(2), timings.Of(MftOpenStep.VolumeHandle)?.Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(500), timings.Of(MftOpenStep.FirstRecord)?.Elapsed);
    }

    /// <summary>
    /// Read the map the way an open does, counting every read through the timings, so each read
    /// lands in whichever step is running when it is made.
    /// </summary>
    private static bool TryRead(FragmentedMftVolume volume, MftOpenTimings timings) =>
        MftExtentMapReader.TryRead(
            volume.Record0,
            FragmentedMftVolume.BytesPerCluster,
            (first, destination) =>
            {
                timings.Read();
                return volume.TryReadClusters(first, destination);
            },
            out _,
            out _,
            timings);
}
