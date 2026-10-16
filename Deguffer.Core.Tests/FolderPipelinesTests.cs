using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="FolderPipelines"/>: which volumes of a scan of several may be scanned at the same time.
/// Apart only where their disks are known to be apart.
/// </summary>
public sealed class FolderPipelinesTests
{
    private static VolumeMedia On(params int[] disks) => new(StorageMedia.Nvme, disks);

    private static Func<string, VolumeMedia?> Storage(params (string Prefix, VolumeMedia? Media)[] answers) =>
        path => answers.FirstOrDefault(answer => path.StartsWith(answer.Prefix, StringComparison.OrdinalIgnoreCase)).Media;

    private static string[][] Letters(IReadOnlyList<IReadOnlyList<VolumeFolders>> pipelines) =>
        [.. pipelines.Select(pipeline => pipeline.Select(volume => volume.DriveLetter?.ToString() ?? "-").ToArray())];

    [Fact]
    public void VolumesOnSeparateDisksAreSeparatePipelines()
    {
        var pipelines = FolderPipelines.Of([@"C:\Photos", @"D:\Music"], Storage((@"C:\", On(0)), (@"D:\", On(1))));

        Assert.Equal([["C"], ["D"]], Letters(pipelines));
    }

    [Fact]
    public void VolumesSharingADiskAreOnePipelineInTheOrderAsked()
    {
        var pipelines = FolderPipelines.Of(
            [@"E:\a", @"C:\b", @"D:\c", @"C:\d"],
            Storage((@"C:\", On(0)), (@"D:\", On(1)), (@"E:\", On(1, 2))));

        Assert.Equal([["E", "D"], ["C"]], Letters(pipelines));
        Assert.Equal([1, 3], pipelines[1][0].Folders.Select(folder => folder.Index));
    }

    /// <summary>A <c>subst</c> letter Windows will not describe could stand for a folder on any disk.</summary>
    [Fact]
    public void AVolumeOnADiskNotKnownPutsEveryVolumeInOnePipeline()
    {
        var notSaid = new VolumeMedia(StorageMedia.Unknown, [], MediaUnknownReason.ExtentsNotRead);

        var pipelines = FolderPipelines.Of(
            [@"C:\a", @"D:\b", @"S:\c"],
            Storage((@"C:\", On(0)), (@"D:\", On(1)), (@"S:\", notSaid)));

        Assert.Equal([["C", "D", "S"]], Letters(pipelines));
    }

    /// <summary>A tuner built without the machine's devices knows no disk, so every search runs as it always did.</summary>
    [Fact]
    public void WithNothingKnownEveryVolumeIsOnePipeline()
    {
        var pipelines = FolderPipelines.Of([@"C:\a", @"D:\b"], _ => null);

        Assert.Equal([["C", "D"]], Letters(pipelines));
    }

    /// <summary>A share is on no disk of this machine, so it is apart from every local volume.</summary>
    [Fact]
    public void AShareIsAPipelineOfItsOwn()
    {
        var pipelines = FolderPipelines.Of(
            [@"C:\a", @"\\server.test\share\b"],
            Storage((@"C:\", On(0)), (@"\\", new VolumeMedia(StorageMedia.Network, []))));

        Assert.Equal([["C"], ["-"]], Letters(pipelines));
        Assert.Null(pipelines[1][0].Folders[0].Volume);
    }

    /// <summary>
    /// A folder can be a mount point of a volume on another disk. Its walk reads that disk, so its
    /// letter's pipeline is the other disk's too.
    /// </summary>
    [Fact]
    public void AFolderMountedFromAnotherDiskJoinsThatDisksPipeline()
    {
        var pipelines = FolderPipelines.Of(
            [@"C:\Mounted\x", @"D:\b"],
            Storage((@"C:\Mounted", On(1)), (@"C:\", On(0)), (@"D:\", On(1))));

        Assert.Equal([["C", "D"]], Letters(pipelines));
    }
}
