using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where a path sits below the top of its volume, which decides both what
/// <see cref="Exploring.Acting.ExploreActionPolicy"/> refuses to remove there and what
/// <see cref="Exploring.Knowledge.ItemGuide"/> explains about it.
///
/// <para>Against <see cref="FakeVolumeInventory"/>, which stands in for Windows' answer to where a
/// path's volume is mounted. So nothing here touches a disk, and the assertions hold on a machine
/// with one drive, one with a share mapped, and one with a volume mounted at a folder. The drive
/// and share cases run with no volumes at all, which is the machine saying nothing, and are
/// answered from the path's own root as they were before the machine was asked.</para>
/// </summary>
public sealed class VolumeRootTests
{
    private readonly FakeVolumeInventory _volumes = new();

    [Theory]
    [InlineData(@"C:\pagefile.sys", "pagefile.sys")]
    [InlineData(@"C:\Windows", "Windows")]
    [InlineData(@"C:\$MFT", "$MFT")]
    [InlineData(@"D:\System Volume Information", "System Volume Information")]
    [InlineData(@"C:\Windows\System32", @"Windows\System32")]
    [InlineData(@"C:\$Extend\$Quota", @"$Extend\$Quota")]
    public void APathOnADriveIsBelowTheDrive(string path, string below) =>
        Assert.Equal(below, VolumeRoot.Below(_volumes, path));

    /// <summary>
    /// A root is not below itself. Both spellings, because <see cref="Path.GetDirectoryName"/>
    /// answers null for each and a rule that read the text instead would have to know which.
    /// </summary>
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share")]
    public void ARootItselfIsNot(string path) => Assert.Null(VolumeRoot.Below(_volumes, path));

    /// <summary>
    /// A share, where the root's shape is not the drive's. <see cref="Path.GetPathRoot(string?)"/>
    /// answers <c>C:\</c> with its separator and <c>\\server\share</c> without one, so the remainder
    /// below the root carries a leading separator here and not there. Taking the remainder as it
    /// comes answers correctly for a drive and wrongly for a share.
    /// </summary>
    [Theory]
    [InlineData(@"\\server\share\pagefile.sys", "pagefile.sys")]
    [InlineData(@"\\server\share\folder\pagefile.sys", @"folder\pagefile.sys")]
    public void APathOnAShareIsBelowTheShare(string path, string below) =>
        Assert.Equal(below, VolumeRoot.Below(_volumes, path));

    /// <summary>
    /// A relative path has no root to sit below. It answers null rather than throwing, because the
    /// callers reach this from a path a scan produced and a refusal to classify is the safe
    /// direction for all of them. The machine is not asked about it, since there is nothing to ask.
    /// </summary>
    [Theory]
    [InlineData("pagefile.sys")]
    [InlineData(@"folder\pagefile.sys")]
    public void ARelativePathIsNot(string path)
    {
        Assert.Null(VolumeRoot.Below(_volumes, path));
        Assert.Empty(_volumes.MountPointQueries);
    }

    /// <summary>
    /// The drive-relative form, which is the one that looks qualified and is not. <c>C:pagefile.sys</c>
    /// means "pagefile.sys in whatever directory this process is standing in on C:", so its root
    /// answers <c>C:</c> and the remainder answers a single segment — the exact shape of a path that
    /// <em>is</em> at a volume root. Nothing here may take that for one.
    /// </summary>
    [Fact]
    public void ADriveRelativePathIsNot() => Assert.Null(VolumeRoot.Below(_volumes, "C:pagefile.sys"));

    /// <summary>
    /// The case the machine is asked for. A volume mounted at <c>C:\Mount</c> has its top there, so
    /// its paging file, its NTFS records and its Recycle Bin are at the top of their volume. Read
    /// from the drive letter they were one level below <c>C:\</c>, under a first segment of
    /// <c>Mount</c>, and none of the rules keyed on those names recognised them.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Mount\pagefile.sys", "pagefile.sys")]
    [InlineData(@"C:\Mount\$MFT", "$MFT")]
    [InlineData(@"C:\Mount\$Recycle.Bin\S-1-5-21-1000", @"$Recycle.Bin\S-1-5-21-1000")]
    [InlineData(@"C:\MOUNT\pagefile.sys", "pagefile.sys")]
    public void APathOnAVolumeMountedAtAFolderIsBelowThatFolder(string path, string below)
    {
        _volumes.With(@"C:\").With(@"D:\", alsoMountedAt: [@"C:\Mount\"]);

        Assert.Equal(below, VolumeRoot.Below(_volumes, path));
    }

    /// <summary>
    /// A volume mounted only at a folder, with no letter of its own, which is the volume the drive
    /// picker lists under that folder's name.
    /// </summary>
    [Fact]
    public void AVolumeWithNoLetterIsAnsweredAtItsFolder()
    {
        _volumes.With(@"C:\").With(@"C:\Mount\");

        Assert.Equal("pagefile.sys", VolumeRoot.Below(_volumes, @"C:\Mount\pagefile.sys"));
    }

    /// <summary>
    /// The folder a volume is mounted at is that volume's root, in both of the spellings a caller
    /// may hold. <see cref="Exploring.Acting.ExploreActionPolicy"/> refuses a root as a whole drive,
    /// and read from the drive letter <c>C:\Mount</c> was an ordinary folder of <c>C:</c>.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Mount")]
    [InlineData(@"C:\Mount\")]
    public void TheFolderAVolumeIsMountedAtIsARoot(string path)
    {
        _volumes.With(@"C:\").With(@"D:\", alsoMountedAt: [@"C:\Mount\"]);

        Assert.Null(VolumeRoot.Below(_volumes, path));
    }

    /// <summary>
    /// A volume mounted inside another volume that is itself mounted at a folder. The deepest mount
    /// point holding the path is the top of the volume the path is on.
    /// </summary>
    [Fact]
    public void AVolumeMountedInsideAMountedVolumeIsBelowTheDeeperFolder()
    {
        _volumes
            .With(@"C:\")
            .With(@"D:\", alsoMountedAt: [@"C:\Mount\"])
            .With(@"E:\", alsoMountedAt: [@"C:\Mount\Inner\"]);

        Assert.Equal("pagefile.sys", VolumeRoot.Below(_volumes, @"C:\Mount\Inner\pagefile.sys"));
        Assert.Equal(@"Other\pagefile.sys", VolumeRoot.Below(_volumes, @"C:\Mount\Other\pagefile.sys"));
    }

    /// <summary>
    /// A folder whose name starts with a mount point's is not inside it. <c>C:\Mountains</c> is a
    /// folder of <c>C:</c>, whatever is mounted at <c>C:\Mount</c>.
    /// </summary>
    [Fact]
    public void AFolderBesideAMountPointIsNotOnTheMountedVolume()
    {
        _volumes.Answering(@"C:\Mount\");

        Assert.Equal(@"Mountains\pagefile.sys", VolumeRoot.Below(_volumes, @"C:\Mountains\pagefile.sys"));
    }

    /// <summary>
    /// Windows answers a path through a junction with the volume on the junction's far side, which
    /// is not a prefix of the path and so names no position in it. The path's own root answers
    /// instead, which is what this type gave before it asked the machine: an answer the path does
    /// not contain can never take a volume root away.
    /// </summary>
    [Fact]
    public void AnAnswerThatIsNotAPrefixOfThePathFallsBackToItsRoot()
    {
        _volumes.Answering(@"D:\");

        Assert.Equal(@"Link\pagefile.sys", VolumeRoot.Below(_volumes, @"C:\Link\pagefile.sys"));
        Assert.Equal("pagefile.sys", VolumeRoot.Below(_volumes, @"C:\pagefile.sys"));
    }

    /// <summary>
    /// The extended-length form is the display form's position (§6.3). Asserted on the form of the
    /// path the machine is asked about, because the real call compares its answer against the path
    /// and <c>\\?\C:\Mount\</c> starts with none of the mount points Windows reports.
    /// </summary>
    [Fact]
    public void AnExtendedLengthPathIsAnsweredAsItsDisplayForm()
    {
        _volumes.With(@"C:\").With(@"D:\", alsoMountedAt: [@"C:\Mount\"]);

        Assert.Equal("pagefile.sys", VolumeRoot.Below(_volumes, @"\\?\C:\Mount\pagefile.sys"));
        Assert.Equal(@"C:\Mount\pagefile.sys", Assert.Single(_volumes.MountPointQueries));
    }
}
