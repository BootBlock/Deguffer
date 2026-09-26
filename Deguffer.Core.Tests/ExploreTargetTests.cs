using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where each way of re-pointing the Explore page leaves the drive and the folder. Every rule here
/// guards one failure: the page stating one target while it scans another.
/// </summary>
public sealed class ExploreTargetTests
{
    private const string Folder = @"C:\Users\testuser\Downloads";

    [Fact]
    public void AChosenFolderWinsOverTheDrive()
    {
        Assert.Equal(Folder, new ExploreTarget(@"C:\", Folder).Root);
        Assert.Equal(@"C:\", new ExploreTarget(@"C:\", null).Root);
        Assert.Null(new ExploreTarget(null, null).Root);
    }

    /// <summary>
    /// Refused by the volume the scan's root is on, not the one the drive box names. A folder on a
    /// cloud mount is the route a reader takes next when the drive is refused, and scanning it is the
    /// same download of everything on it.
    /// </summary>
    [Fact]
    public void AFolderOnCloudStorageIsRefusedWhateverTheDriveBoxSays()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\")
            .With(@"C:\Cloud\", features: VolumeFeatures.RemoteStorage);

        var target = new ExploreTarget(@"C:\", @"C:\Cloud\Photos");

        Assert.Equal(DriveChoice.RemoteStorageRefusal, target.Refusal(volumes));
        Assert.False(target.IsScannable(volumes));
        Assert.Null(new ExploreTarget(@"C:\", Folder).Refusal(volumes));
        Assert.True(new ExploreTarget(@"C:\", Folder).IsScannable(volumes));
    }

    /// <summary>A share the inventory says nothing about has no flags to refuse on, and refusing on none would be a guess.</summary>
    [Fact]
    public void AFolderOnAVolumeNothingMeasuredIsNotRefused()
    {
        var target = new ExploreTarget(null, @"\\server.test\share\folder");

        Assert.Null(target.Refusal(new FakeVolumeInventory().With(@"C:\")));
    }

    [Fact]
    public void NothingToScanIsNotScannable()
    {
        Assert.False(new ExploreTarget(null, null).IsScannable(new FakeVolumeInventory()));
    }

    /// <summary>Choosing a drive is choosing to scan the whole of it.</summary>
    [Fact]
    public void ChoosingADriveDropsTheFolder()
    {
        Assert.Equal(new ExploreTarget(@"D:\", null), new ExploreTarget(@"C:\", Folder).Choosing(@"D:\"));
        Assert.Equal(new ExploreTarget(@"C:\", null), new ExploreTarget(@"C:\", Folder).WholeDrive());
    }

    /// <summary>
    /// The drive box follows a chosen folder onto the volume holding it, and stays where it was
    /// where the picker offers no such volume.
    /// </summary>
    [Fact]
    public void TheDriveBoxFollowsAChosenFolderWhereItCan()
    {
        var target = new ExploreTarget(@"D:\", null);

        Assert.Equal(new ExploreTarget(@"C:\", Folder), target.ScopedTo(Folder, holdingDrive: @"C:\"));
        Assert.Equal(new ExploreTarget(@"D:\", Folder), target.ScopedTo(Folder, holdingDrive: null));
    }

    /// <summary>
    /// A reading that hands the chosen drive back is not a choice and keeps the folder, whichever
    /// way the drive is spelled.
    /// </summary>
    [Fact]
    public void AReadingThatKeepsTheDriveKeepsTheFolder()
    {
        Assert.Equal(new ExploreTarget(@"c:\", Folder), new ExploreTarget(@"C:\", Folder).AfterReading(@"c:\"));
    }

    /// <summary>
    /// A reading that finds the chosen drive gone has moved the page to a volume nobody picked, which
    /// is a change of drive however it came about, and a folder on the drive that went goes with it.
    /// </summary>
    [Fact]
    public void AReadingThatLosesTheDriveDropsTheFolder()
    {
        Assert.Equal(new ExploreTarget(@"C:\", null), new ExploreTarget(@"E:\", @"E:\Photos").AfterReading(@"C:\"));
        Assert.Equal(new ExploreTarget(null, null), new ExploreTarget(@"E:\", @"E:\Photos").AfterReading(null));
    }

    /// <summary>
    /// A page that named no drive has not moved off one. A folder restored before the list was read
    /// is still what the reader asked to scan.
    /// </summary>
    [Fact]
    public void AReadingThatGivesTheBoxItsFirstDriveKeepsTheFolder()
    {
        Assert.Equal(new ExploreTarget(@"C:\", Folder), new ExploreTarget(null, Folder).AfterReading(@"C:\"));
    }

    /// <summary>
    /// A drive that is no longer mounted, with no folder beside it, restores nothing, and the page
    /// must not scan the volume its box defaulted to on the strength of it.
    /// </summary>
    [Fact]
    public void PointingAtAGoneDriveAndNoFolderRestoresNothing()
    {
        var target = new ExploreTarget(@"C:\", null);

        Assert.Null(target.Pointing(offeredDrive: null, folder: null));
        Assert.Equal(new ExploreTarget(@"D:\", null), target.Pointing(@"D:\", null));
        Assert.Equal(new ExploreTarget(@"C:\", @"E:\Photos"), target.Pointing(null, @"E:\Photos"));
        Assert.Equal(new ExploreTarget(@"D:\", @"D:\Photos"), target.Pointing(@"D:\", @"D:\Photos"));
    }

    [Fact]
    public void TwoTargetsScanTheSameWhereTheirRootsAgree()
    {
        Assert.True(new ExploreTarget(@"C:\", Folder).ScansTheSameAs(new ExploreTarget(@"D:\", Folder.ToUpperInvariant())));
        Assert.False(new ExploreTarget(@"C:\", null).ScansTheSameAs(new ExploreTarget(@"D:\", null)));
    }
}
