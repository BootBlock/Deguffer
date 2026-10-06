using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A folder compared with another at every path either is reachable at, so a check of one holding the
/// other is asked of the folders rather than of how a setting named them.
/// </summary>
public sealed class ReachedFolderTests
{
    private readonly FakeVolumeInventory _volumes = new();

    private ReachedFolder At(string path) => ReachedFolder.At(path, _volumes);

    /// <summary>
    /// Either side may be a setting, so each is followed to where it is. A folder beside the other,
    /// reached the same way, is neither held nor the same (§5.6).
    /// </summary>
    [Fact]
    public void ComparesFoldersNamedThroughDifferentLetters()
    {
        _volumes.Substituting(@"S:\", @"D:\shared").Substituting(@"T:\", @"D:\shared\cache");

        Assert.True(At(@"S:\cache").Holds(At(@"T:\Temp")));
        Assert.True(At(@"T:\Temp").IsSameAs(At(@"S:\cache\Temp")));
        Assert.False(At(@"S:\gradle").Holds(At(@"T:\Temp")));
        Assert.False(At(@"S:\gradle\Temp").IsSameAs(At(@"T:\Temp")));
        Assert.False(At(@"T:\Temp").Holds(At(@"S:\cache")));
    }

    /// <summary>
    /// A folder reached through another mount of its volume is the same folder at both.
    /// </summary>
    [Fact]
    public void ComparesAFolderReachedThroughAnotherMountOfItsVolume()
    {
        _volumes.With(@"C:\", alsoMountedAt: [@"Q:\SysMount\"]);

        Assert.True(At(@"Q:\SysMount\Users\testuser").Holds(At(@"C:\Users\testuser\AppData")));
        Assert.True(At(@"\\?\Q:\SysMount\Users\testuser\").IsSameAs(At(@"C:\Users\testuser")));
        Assert.False(At(@"Q:\Users\testuser").IsSameAs(At(@"C:\Users\testuser")));
    }
}
