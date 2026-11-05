using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where a drive is mounted is asked once per mount point for the life of an owner that already
/// remembers each path's answer, because nearly every path is on one of a few drives (#513). A volume
/// mounted at a folder since the first question is still seen at every path under that folder, and
/// a volume reached at a second mount point is still refused at both (#144, #146).
/// </summary>
public sealed class RememberedMountPointsTests
{
    private const string Profile = @"C:\Users\testuser";

    private readonly FakeVolumeInventory _volumes = new FakeVolumeInventory()
        .With(@"C:\", alsoMountedAt: [@"Q:\SysMount\"])
        .With(@"D:\");

    /// <summary>
    /// A hundred folders on one drive ask where it is mounted once, and every one of them is still
    /// reachable through the drive's other mount point.
    /// </summary>
    [Fact]
    public void AHundredFoldersOnOneDriveAskWhereItIsMountedOnce()
    {
        var remembered = new RememberedMountPoints(_volumes);

        var folders = Enumerable.Range(0, 100)
            .Select(index => ReachedFolder.At($@"{Profile}\src\project{index}", remembered))
            .ToList();

        Assert.Equal([@"C:\"], _volumes.MountPointsQueries);
        Assert.All(folders, (folder, index) => Assert.Contains($@"Q:\SysMount\Users\testuser\src\project{index}", folder.Places));
    }

    /// <summary>
    /// A volume mounted at a folder after the drive was asked about is seen at every path under that
    /// folder, because a path there is answered with the new mount point, which nothing remembers.
    /// The system folder reached through it reads as the system folder (#144).
    /// </summary>
    [Fact]
    public void AVolumeMountedAtAFolderSinceTheFirstQuestionIsSeenUnderThatFolder()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\").With(@"D:\");
        var remembered = new RememberedMountPoints(volumes);

        ReachedFolder.At(@"C:\Users\testuser\src", remembered);
        ReachedFolder.At(@"D:\data", remembered);

        volumes.Without(@"C:\").With(@"C:\", alsoMountedAt: [@"D:\SysMount\"]);

        var places = VolumeRoot.Places(remembered, @"D:\SysMount\Windows\System32");

        Assert.NotNull(places);
        Assert.Contains(places, place => place.Path == @"C:\Windows\System32");
    }

    /// <summary>
    /// The volume's top reached through its second mount point is a volume top at both (#146).
    /// </summary>
    [Fact]
    public void AVolumeTopReachedAtItsSecondMountPointIsATopAtBoth()
    {
        var remembered = new RememberedMountPoints(_volumes);

        ReachedFolder.At(@"C:\Users\testuser", remembered);

        var top = ReachedFolder.At(@"Q:\SysMount", remembered);

        Assert.True(top.IsVolumeTop);
        Assert.Contains(@"C:\", top.Places);
    }

    /// <summary>A mount point with and without its trailing separator is one directory, asked about once.</summary>
    [Fact]
    public void BothSpellingsOfAMountPointShareOneAnswer()
    {
        var remembered = new RememberedMountPoints(_volumes);

        var named = remembered.MountPointsOf(@"Q:\SysMount");
        var separated = remembered.MountPointsOf(@"Q:\SysMount\");

        Assert.Single(_volumes.MountPointsQueries);
        Assert.Equal(named, separated);
    }

    /// <summary>
    /// Forgetting is the start of the next pass: the drive is asked about again, and a mount made in
    /// between is in the answer.
    /// </summary>
    [Fact]
    public void ForgettingAsksTheMachineAgain()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\");
        var remembered = new RememberedMountPoints(volumes);

        Assert.Equal([@"C:\"], remembered.MountPointsOf(@"C:\"));

        volumes.Without(@"C:\").With(@"C:\", alsoMountedAt: [@"Q:\SysMount\"]);
        remembered.Forget();

        Assert.Equal([@"C:\", @"Q:\SysMount\"], remembered.MountPointsOf(@"C:\"));
        Assert.Equal(2, volumes.MountPointsQueries.Count);
    }

    /// <summary>
    /// Invalidating asks for what was mounted since to be seen, so it forgets the answers as well as
    /// discarding the list of the volumes underneath.
    /// </summary>
    [Fact]
    public void InvalidatingForgetsAndInvalidatesTheVolumes()
    {
        var remembered = new RememberedMountPoints(_volumes);

        remembered.MountPointsOf(@"C:\");
        remembered.Invalidate();
        remembered.MountPointsOf(@"C:\");

        Assert.Equal(1, _volumes.InvalidateCount);
        Assert.Equal(2, _volumes.MountPointsQueries.Count);
    }

    /// <summary>
    /// Where a path is mounted and what a letter stands for are asked at every call: a letter
    /// <c>subst</c> made after the first question is followed to its folder.
    /// </summary>
    [Fact]
    public void WhereAPathIsMountedAndWhatALetterStandsForAreAskedEachTime()
    {
        var remembered = new RememberedMountPoints(_volumes);

        Assert.Null(remembered.SubstituteOf(@"S:\"));

        _volumes.Substituting(@"S:\", Profile);

        Assert.Equal(Profile, remembered.SubstituteOf(@"S:\"));

        remembered.MountPointOf(@"C:\Users");
        remembered.MountPointOf(@"C:\Users");

        Assert.Equal(2, _volumes.MountPointQueries.Count);
    }
}
