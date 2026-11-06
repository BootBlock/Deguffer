using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where a volume is mounted is asked once per volume for the life of an owner that already remembers
/// each path's answer, because nearly every path is on one of a few drives (#513). Which volume a
/// mount point names is asked each time, so a letter given to another volume is answered with that
/// volume's places. A volume mounted at a folder since the first question is still seen at every path
/// under that folder, and a volume reached at a second mount point is still refused at both (#144,
/// #146).
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

        Assert.Single(_volumes.MountPointsQueries);
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

    /// <summary>
    /// Every mount point of one volume, in either spelling, names the same volume, which is asked
    /// where it is mounted once.
    /// </summary>
    [Fact]
    public void EveryMountPointOfOneVolumeSharesOneAnswer()
    {
        var remembered = new RememberedMountPoints(_volumes);

        var named = MountPointsOf(remembered, @"Q:\SysMount");
        var separated = MountPointsOf(remembered, @"Q:\SysMount\");
        var letter = MountPointsOf(remembered, @"C:\");

        Assert.Single(_volumes.MountPointsQueries);
        Assert.Equal([@"C:\", @"Q:\SysMount\"], named);
        Assert.Equal(named, separated);
        Assert.Equal(named, letter);
    }

    /// <summary>
    /// A drive letter given to another volume after it was asked about is answered with the new
    /// volume's places, so a tool's folder written at the new volume's folder mount is refused when
    /// reached through the letter (§5.2).
    /// </summary>
    [Fact]
    public void ALetterGivenToAnotherVolumeIsAnsweredWithThatVolumesPlaces()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\", volumeName: @"\\?\Volume{system}\")
            .With(@"C:\Dev\", volumeName: @"\\?\Volume{development}\")
            .With(@"E:\", DriveType.Removable, volumeName: @"\\?\Volume{stick}\");
        var remembered = new RememberedMountPoints(volumes);

        Assert.Equal([@"E:\"], MountPointsOf(remembered, @"E:\"));

        volumes.Without(@"E:\").Without(@"C:\Dev\")
            .With(@"C:\Dev\", alsoMountedAt: [@"E:\"], volumeName: @"\\?\Volume{development}\");

        var places = VolumeRoot.Places(remembered, @"E:\cargo");

        Assert.NotNull(places);
        Assert.Contains(places, place => place.Path == @"C:\Dev\cargo");
    }

    /// <summary>
    /// A volume named a moment ago is mounted somewhere, so an answer of nowhere is a call that
    /// failed. It is not kept, and the next question finds the system folder at the volume's other
    /// mount point (#144).
    /// </summary>
    [Fact]
    public void AFailedAnswerIsNotRemembered()
    {
        var remembered = new RememberedMountPoints(_volumes);
        _volumes.FailingMountPointQueries = 1;

        Assert.Empty(MountPointsOf(remembered, @"C:\"));

        var places = VolumeRoot.Places(remembered, @"Q:\SysMount\Windows\System32");

        Assert.NotNull(places);
        Assert.Contains(places, place => place.Path == @"C:\Windows\System32");
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

        Assert.Equal([@"C:\"], MountPointsOf(remembered, @"C:\"));

        volumes.Without(@"C:\").With(@"C:\", alsoMountedAt: [@"Q:\SysMount\"]);
        remembered.Forget();

        Assert.Equal([@"C:\", @"Q:\SysMount\"], MountPointsOf(remembered, @"C:\"));
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

        MountPointsOf(remembered, @"C:\");
        remembered.Invalidate();
        MountPointsOf(remembered, @"C:\");

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

    /// <summary>The question every caller asks, which the interface composes for every implementation.</summary>
    private static IReadOnlyList<string> MountPointsOf(IVolumeInventory volumes, string mountPoint) =>
        volumes.MountPointsOf(mountPoint);
}
