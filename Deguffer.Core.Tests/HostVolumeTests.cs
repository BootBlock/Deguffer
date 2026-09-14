using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which volume a path is on. Three callers decide whether to read a location on this answer, so the
/// cases that matter are the two non-answers: a path on a volume nothing was measured for, and a path
/// carrying the extended-length prefix §6.3 requires.
/// </summary>
public sealed class HostVolumeTests
{
    /// <summary>The flag word measured on a Google Drive mount. See <see cref="LocalVolume.StoresContentRemotely"/>.</summary>
    private const VolumeFeatures CloudMount = (VolumeFeatures)0x0000_0106;

    private static readonly FakeVolumeInventory Machine = new FakeVolumeInventory()
        .With(@"C:\")
        .With(@"V:\", features: CloudMount);

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
    /// for: a share, a volume mounted without a letter, a drive that is not attached. No caller may
    /// read it as an all-clear, and none may refuse on it either — refusing on no reading would be a
    /// guess.
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
    /// A path on a volume mounted at a directory answers as the volume that directory is on. This is
    /// the one case that answers <em>wrongly</em> rather than not at all, and it is recorded here
    /// rather than left for a reader to discover: <c>DriveInfo.GetDrives</c> reports drive letters
    /// only, so such a volume is never in the inventory to be matched, and the mount point's own
    /// volume is what the comparison finds.
    ///
    /// <para>Asserted rather than fixed because the fix is a change to what
    /// <see cref="IVolumeInventory"/> enumerates, not to how <see cref="HostVolume"/> reads it. A
    /// cloud client mounted at a folder is therefore searched, and the doc on
    /// <see cref="HostVolume.For"/> says so.</para>
    /// </summary>
    [Fact]
    public void AnswersAFolderMountedVolumeAsTheVolumeItsMountPointIsOn()
    {
        var mounted = HostVolume.For(Machine, @"C:\Mount\work");

        Assert.Equal(@"C:\", mounted?.RootPath);
        Assert.False(mounted?.StoresContentRemotely);
    }
}
