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
    /// The share case, which is the one that fails if the trailing separator is compared as it
    /// arrives: <c>GetPathRoot</c> keeps it here and <c>GetDirectoryName</c> does not.
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
}
