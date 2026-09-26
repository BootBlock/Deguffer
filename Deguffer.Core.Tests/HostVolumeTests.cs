using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which volume a path is on. Three callers decide whether to read a location on this answer, so the
/// cases that matter are the two non-answers — a path on a volume nothing was measured for, and a path
/// carrying the extended-length prefix §6.3 requires — and the volume mounted at a folder, which is the
/// one case that used to answer confidently and wrongly.
/// </summary>
public sealed class HostVolumeTests
{
    /// <summary>The flag word measured on a Google Drive mount. See <see cref="LocalVolume.StoresContentRemotely"/>.</summary>
    private const VolumeFeatures CloudMount = (VolumeFeatures)0x0000_0106;

    private static readonly FakeVolumeInventory Machine = new FakeVolumeInventory()
        .With(@"C:\")
        .With(@"V:\", features: CloudMount);

    /// <summary>
    /// A cloud client mounted at a folder rather than under a letter, inside the local disk that
    /// holds its mount point — the arrangement both volumes have to be told apart in.
    /// </summary>
    private static FakeVolumeInventory FolderMounted(bool mountedFirst = false)
    {
        var machine = new FakeVolumeInventory();

        // Both orders, because the inventory's order is Windows' own and a rule that read the first
        // match rather than the longest would pass in one of them.
        if (mountedFirst)
        {
            return machine.With(@"C:\Mount\", features: CloudMount).With(@"C:\");
        }

        return machine.With(@"C:\").With(@"C:\Mount\", features: CloudMount);
    }

    [Fact]
    public void FindsTheVolumeAPathIsOn()
    {
        Assert.Equal(@"V:\", HostVolume.For(Machine, @"V:\work\app")?.RootPath);
        Assert.Equal(@"C:\", HostVolume.For(Machine, @"C:\Users\testuser\src")?.RootPath);
    }

    /// <summary>
    /// A volume root is on its own volume. <c>C:\</c> keeps the trailing separator no other path has,
    /// which is why the comparison goes through <see cref="LongPath.Contains"/> rather than by
    /// appending one.
    /// </summary>
    [Fact]
    public void FindsTheVolumeForTheVolumeRootItself()
    {
        Assert.Equal(@"V:\", HostVolume.For(Machine, @"V:\")?.RootPath);
    }

    /// <summary>
    /// The reading a caller acts on, rather than the mount point. Nothing else about the volume
    /// distinguishes a cloud client's mount from a disk.
    /// </summary>
    [Fact]
    public void ReportsWhetherTheVolumeHoldingAPathStoresItsContentElsewhere()
    {
        Assert.True(HostVolume.For(Machine, @"V:\work")?.StoresContentRemotely);
        Assert.False(HostVolume.For(Machine, @"C:\Users\testuser\src")?.StoresContentRemotely);
    }

    /// <summary>
    /// §6.3 puts every path in Core through <see cref="LongPath"/>, so a path arrives here in
    /// extended-length form as often as not — and <c>\\?\V:\work</c> starts with none of the roots the
    /// inventory reports. Stripping the prefix is what makes the answer the same either way, and
    /// asserting the two forms agree is what discriminates: a deep tree would answer identically with
    /// the stripping removed.
    /// </summary>
    [Fact]
    public void ReadsThroughTheExtendedLengthPrefix()
    {
        Assert.Equal(@"V:\", HostVolume.For(Machine, LongPath.Extended(@"V:\work\app"))?.RootPath);
        Assert.Equal(
            HostVolume.For(Machine, @"V:\work\app"),
            HostVolume.For(Machine, LongPath.Extended(@"V:\work\app")));
    }

    /// <summary>
    /// Null is "nothing was measured", and it is the answer for every path the inventory has no volume
    /// for: a share, a volume this machine has mounted nowhere, a drive that is not attached. No caller
    /// may read it as an all-clear, and none may refuse on it either — refusing on no reading would be
    /// a guess.
    /// </summary>
    [Theory]
    [InlineData(@"\\server\share\work")]
    [InlineData(@"D:\work")]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\work")]
    [InlineData("relative")]
    public void AnswersNothingForAPathOnNoVolumeItKnowsAbout(string path)
    {
        Assert.Null(HostVolume.For(Machine, path));
    }

    /// <summary>
    /// A volume whose letter is a prefix of another's must not answer for it. The comparison is over
    /// whole path segments, which is what keeps <c>C:\</c> from claiming a path on <c>CC:</c>-shaped
    /// nonsense and, more usefully, keeps the drive letters apart at all.
    /// </summary>
    [Fact]
    public void DoesNotConfuseOneVolumeWithAnother()
    {
        Assert.Equal(@"C:\", HostVolume.For(Machine, @"C:\V\work")?.RootPath);
    }

    /// <summary>
    /// A path on a volume mounted at a directory answers as that volume, not as the volume its mount
    /// point sits on. Both hold the path — a folder mount point is inside another volume by
    /// construction — so the longer of the two is the one the bytes are actually on.
    ///
    /// <para>The case this whole rule exists for: a cloud client mounted this way was reported as the
    /// local disk holding its mount point, which is a confident reading of the wrong volume rather
    /// than a non-answer, and every caller that refuses on
    /// <see cref="LocalVolume.StoresContentRemotely"/> was therefore told it could read it.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnswersAFolderMountedVolumeAsItself(bool mountedFirst)
    {
        var mounted = HostVolume.For(FolderMounted(mountedFirst), @"C:\Mount\work");

        Assert.Equal(@"C:\Mount\", mounted?.RootPath);
        Assert.True(mounted?.StoresContentRemotely);
    }

    /// <summary>
    /// The mount point directory itself, named without a trailing separator, which is the form
    /// <see cref="LongPath.Configured(string?)"/> produces and so the form a stored source root
    /// arrives in. <c>C:\Mount</c> is not a prefix of <c>C:\Mount\</c>, so a comparison by prefix
    /// alone hands the root of the mounted volume to the volume underneath it.
    /// </summary>
    [Fact]
    public void AnswersTheMountPointDirectoryItselfAsTheMountedVolume()
    {
        Assert.Equal(@"C:\Mount\", HostVolume.For(FolderMounted(), @"C:\Mount")?.RootPath);
    }

    /// <summary>
    /// A path beside the mount point is still on the volume underneath. The longest-match rule must
    /// not widen a folder-mounted volume's claim past the folder it is mounted at.
    /// </summary>
    [Fact]
    public void LeavesAPathBesideTheMountPointOnTheVolumeUnderneath()
    {
        var beside = HostVolume.For(FolderMounted(), @"C:\Mountains\work");

        Assert.Equal(@"C:\", beside?.RootPath);
        Assert.False(beside?.StoresContentRemotely);
    }

    /// <summary>
    /// One volume, mounted both under a letter and at a folder on another volume. Every one of its
    /// mount points answers for it, and the answer is the same volume — which is what keeps a rule
    /// reading <see cref="LocalVolume.StoresContentRemotely"/> from depending on which name the user
    /// happened to type.
    /// </summary>
    [Fact]
    public void AnswersForEveryPathAVolumeIsMountedAt()
    {
        var machine = new FakeVolumeInventory()
            .With(@"C:\")
            .With(@"D:\", features: CloudMount, alsoMountedAt: [@"C:\Mount\"]);

        Assert.Equal(@"D:\", HostVolume.For(machine, @"D:\work")?.RootPath);
        Assert.Equal(@"D:\", HostVolume.For(machine, @"C:\Mount\work")?.RootPath);
        Assert.True(HostVolume.For(machine, @"C:\Mount\work")?.StoresContentRemotely);
    }
}
