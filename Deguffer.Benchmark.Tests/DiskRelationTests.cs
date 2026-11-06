using Deguffer.Core.Scanning.Media;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// Where the race's build reads against where its walk lists, which a result states because it
/// separates a cost the drive imposes from one the process imposes on itself.
/// </summary>
public sealed class DiskRelationTests
{
    [Fact]
    public void TheFoldersOwnVolumeIsTheSameVolumeWhateverItsDisksSay() =>
        Assert.Equal(DiskRelation.SameVolume, DiskRelations.Of('c', null, 'C', null));

    [Fact]
    public void AnotherVolumeOnADiskTheFolderIsOnSharesTheDisk() =>
        Assert.Equal(DiskRelation.SameDisk, DiskRelations.Of('C', On(0, 1), 'D', On(1)));

    [Fact]
    public void AVolumeOnNoDiskTheFolderIsOnIsOnAnotherDisk() =>
        Assert.Equal(DiskRelation.OtherDisk, DiskRelations.Of('C', On(0), 'D', On(1)));

    /// <summary>A drive that did not name its disks says nothing either way, so it is not called another disk.</summary>
    [Fact]
    public void ADriveThatNamedNoDiskIsUnknown()
    {
        Assert.Equal(DiskRelation.Unknown, DiskRelations.Of('C', On(0), 'D', On()));
        Assert.Equal(DiskRelation.Unknown, DiskRelations.Of('C', null, 'D', On(1)));
    }

    private static VolumeMedia On(params int[] disks) => new(StorageMedia.Nvme, disks);
}
