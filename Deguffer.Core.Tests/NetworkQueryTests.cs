using Deguffer.Core.Exploring.Network;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the Path… box's text asks for: everything this account uses while it is empty, then
/// computers, then a computer's shares, then a share's folders, each narrowed by the part of a name
/// after the last separator.
/// </summary>
public sealed class NetworkQueryTests
{
    [Theory]
    [InlineData("", NetworkQueryStage.Everything, "", "")]
    [InlineData("   ", NetworkQueryStage.Everything, "", "")]
    [InlineData(@"\\", NetworkQueryStage.Computers, "", "")]
    [InlineData(@"\\na", NetworkQueryStage.Computers, "", "na")]
    [InlineData(@"\\nas.test\", NetworkQueryStage.Shares, "nas.test", "")]
    [InlineData(@"\\nas.test\me", NetworkQueryStage.Shares, "nas.test", "me")]
    [InlineData(@"\\nas.test\media\", NetworkQueryStage.Folders, @"\\nas.test\media", "")]
    [InlineData(@"\\nas.test\media\Ph", NetworkQueryStage.Folders, @"\\nas.test\media", "Ph")]
    [InlineData(@"\\nas.test\media\Photos\2026\Ju", NetworkQueryStage.Folders, @"\\nas.test\media\Photos\2026", "Ju")]
    [InlineData(@"  ""\\nas.test\me", NetworkQueryStage.Shares, "nas.test", "me")]
    public void EachStageOfASharePathAsksForTheNextLevel(string typed, NetworkQueryStage stage, string parent, string text) =>
        Assert.Equal(new NetworkQuery(stage, parent, text), NetworkQuery.Read(typed));

    /// <summary>A local path, a device path, and a path with an empty name in it are completed by no list.</summary>
    [Theory]
    [InlineData(@"D:\Folder")]
    [InlineData("nas")]
    [InlineData(@"\")]
    [InlineData(@"\\?\UNC\nas.test\media")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\\media")]
    [InlineData(@"\\nas.test\\Photos")]
    public void APathNoListCompletesAsksForNothing(string typed) =>
        Assert.Equal(NetworkQueryStage.None, NetworkQuery.Read(typed).Stage);
}
