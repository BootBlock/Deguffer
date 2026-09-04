using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether a path sits directly at the top of its volume, which decides both what
/// <see cref="Exploring.Acting.ExploreActionPolicy"/> refuses to remove there and what
/// <see cref="Exploring.Knowledge.ItemGuide"/> explains about it.
///
/// <para>Text only, and deliberately: nothing here touches a disk, so the assertions hold on a
/// machine with one drive and on a machine with a share mapped.</para>
/// </summary>
public sealed class VolumeRootTests
{
    [Theory]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\$MFT")]
    [InlineData(@"D:\System Volume Information")]
    public void ADirectChildOfADriveIsAtTheRoot(string path) => Assert.True(VolumeRoot.Holds(path));

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Users\testuser\AppData")]
    [InlineData(@"C:\a\b\c")]
    public void AnythingDeeperIsNot(string path) => Assert.False(VolumeRoot.Holds(path));

    /// <summary>
    /// A root is not a child of itself. Both spellings, because <see cref="Path.GetDirectoryName"/>
    /// answers null for each and a rule that read the text instead would have to know which.
    /// </summary>
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share")]
    public void ARootItselfIsNot(string path) => Assert.False(VolumeRoot.Holds(path));

    /// <summary>
    /// A share, where the root's shape is not the drive's. <see cref="Path.GetPathRoot(string?)"/>
    /// answers <c>C:\</c> with its separator and <c>\\server\share</c> without one, so the remainder
    /// below the root carries a leading separator here and not there. Taking the remainder as it
    /// comes answers correctly for a drive and wrongly for a share.
    /// </summary>
    [Fact]
    public void ADirectChildOfAShareIsAtTheRoot() =>
        Assert.True(VolumeRoot.Holds(@"\\server\share\pagefile.sys"));

    [Fact]
    public void SomethingDeeperInAShareIsNot() =>
        Assert.False(VolumeRoot.Holds(@"\\server\share\folder\pagefile.sys"));

    /// <summary>
    /// A relative path has no root to sit at the top of. It answers false rather than throwing,
    /// because the callers reach this from a path a scan produced and a refusal to classify is the
    /// safe direction for both of them.
    /// </summary>
    [Theory]
    [InlineData("pagefile.sys")]
    [InlineData(@"folder\pagefile.sys")]
    public void ARelativePathIsNot(string path) => Assert.False(VolumeRoot.Holds(path));

    /// <summary>
    /// The drive-relative form, which is the one that looks qualified and is not. <c>C:pagefile.sys</c>
    /// means "pagefile.sys in whatever directory this process is standing in on C:", so its root
    /// answers <c>C:</c> and the remainder answers a single segment — the exact shape of a path that
    /// <em>is</em> at a volume root. Nothing here may take that for one.
    /// </summary>
    [Fact]
    public void ADriveRelativePathIsNot() => Assert.False(VolumeRoot.Holds("C:pagefile.sys"));
}
