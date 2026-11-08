using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A slow build is traced to its stage only where every stage is timed apart. A stage reported
/// twice, out of order or not at all would put the time in the wrong place, and the totals alone
/// cannot say which stage a build spent its time on.
/// </summary>
public sealed class MftStageTimingsTests
{
    private const uint Folder = 16;

    private static readonly MftBuildStage[] ReadStages =
    [
        MftBuildStage.BitmapRead,
        MftBuildStage.FirstPass,
        MftBuildStage.ExtensionLists,
        MftBuildStage.DeferredRecords,
    ];

    /// <summary>
    /// A folder, a file whose attribute list is kept outside the table (so it is held back to the
    /// end of the first pass), and a file with a second name in the folder and one under the root.
    /// </summary>
    private static MftFixture Table() => new MftFixture()
        .AddDirectory(Folder, MftRecord.RootRecordNumber, "folder")
        .AddFileWithANonResidentAttributeList(17, Folder, "spilt.bin", allocated: 8192, logical: 8000, extension: 18, listCluster: 500)
        .AddHardLinkedFile(19, [(Folder, "linked.bin"), (MftRecord.RootRecordNumber, "linked.bin")], allocated: 4096, logical: 4000);

    [Fact]
    public void AReadOfTheTableReportsEachOfItsStagesOnceAndInOrder()
    {
        using var source = Table().Build();
        var timings = new MftStageTimings();

        Assert.True(MftRecordStream.TryReadAll(
            source, (int)source.RecordCount, TableTuning.Default, static (_, _, in _, _, _) => true, onProgress: null, default, timings));

        Assert.Equal(ReadStages, timings.Stages.Select(timed => timed.Stage));
        Assert.Equal(1, timings.DeferredRecords);
    }

    [Fact]
    public void AnIndexBuildReportsEachOfItsStagesOnceAndInOrder()
    {
        using var source = Table().Build();
        var timings = new MftStageTimings();

        Assert.True(MftVolumeIndexBuilder.TryBuild(source, TableTuning.Default, out _, default, timings));

        Assert.Equal([.. ReadStages, MftBuildStage.LinkPlacement, MftBuildStage.ChildLinks], timings.Stages.Select(timed => timed.Stage));
        Assert.Equal(1, timings.DeferredRecords);
        Assert.Equal(1, timings.LinksPlaced);
    }

    [Fact]
    public void AnExploreReadReportsEachOfItsStagesOnceAndInOrder()
    {
        using var source = Table().Build();
        var timings = new MftStageTimings();

        var read = MftExploreReader.Read(source, @"C:\", [], TableTuning.Default, progress: null, default, timings: timings);

        Assert.NotNull(read.Tree);
        Assert.Equal([.. ReadStages, MftBuildStage.LinkPlacement, MftBuildStage.TreeAssembly], timings.Stages.Select(timed => timed.Stage));
        Assert.Equal(1, timings.DeferredRecords);
        Assert.Equal(1, timings.LinksPlaced);
    }

    /// <summary>
    /// Timing a build changes nothing it builds: the same table measures the same with the timer as
    /// without it, so what the benchmark times is what the app builds.
    /// </summary>
    [Fact]
    public void AnIndexBuiltWhileTimedMeasuresAsOneBuiltWithout()
    {
        using var untimedSource = Table().Build();
        using var timedSource = Table().Build();

        Assert.True(MftVolumeIndexBuilder.TryBuild(untimedSource, TableTuning.Default, out var untimed));
        Assert.True(MftVolumeIndexBuilder.TryBuild(timedSource, TableTuning.Default, out var timed, default, new MftStageTimings()));

        Assert.Equal(untimed.TryMeasure(["folder"]), timed.TryMeasure(["folder"]));
        Assert.Equal(untimed.TryMeasure([]), timed.TryMeasure([]));
        Assert.Equal(12_000, timed.TryMeasure(["folder"])!.Value.Logical);
    }

    /// <summary>A read that stopped at its first pass reports the stages it ran and no others.</summary>
    [Fact]
    public void AReadAbandonedInItsFirstPassReportsNoLaterStage()
    {
        using var source = Table().Build();
        var timings = new MftStageTimings();

        Assert.False(MftRecordStream.TryReadAll(
            source, (int)source.RecordCount, TableTuning.Default, static (_, _, in _, _, _) => false, onProgress: null, default, timings));

        Assert.Equal([MftBuildStage.BitmapRead, MftBuildStage.FirstPass], timings.Stages.Select(timed => timed.Stage));
        Assert.Equal(TimeSpan.Zero, timings.Of(MftBuildStage.DeferredRecords));
    }
}
