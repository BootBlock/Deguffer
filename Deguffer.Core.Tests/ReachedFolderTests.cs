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
    /// <summary>
    /// The top of a letter <c>subst</c> made is the folder it stands for as well, so a program working
    /// at <c>S:\</c> is in that folder. A folder beside it is not held (§5.6), and the top is still a
    /// top, which is never removed.
    /// </summary>
    [Fact]
    public void FollowsTheTopOfASubstitutedLetterToItsFolder()
    {
        _volumes.Substituting(@"S:\", @"C:\Source\app").Substituting(@"T:\", @"S:\");

        var top = At(@"T:\");

        Assert.True(top.IsVolumeTop);
        Assert.True(At(@"C:\Source").Holds(top));
        Assert.True(At(@"C:\Source\app").IsSameAs(top));
        Assert.False(At(@"C:\Source\other").Holds(top));
    }

    /// <summary>
    /// A folder asked about by its 8.3 alias keeps that spelling beside the expansion, so a rule spelled
    /// with the alias, whose expansion Windows refused when the rule was followed, still holds what is
    /// spelled the same way. A folder beside it spelled the same way is not held (§5.6).
    ///
    /// <para>On a volume that creates no 8.3 aliases there is no short spelling to ask by, and the
    /// test ends once it has found that, rather than failing.</para>
    /// </summary>
    [Fact]
    public void KeepsTheSpellingAFolderWasAskedAboutByBesideItsExpansion()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("LongToolFolderName", "tool");
        temp.CreateDirectory("LongToolFolderName", "beside");

        if (ShortPath.Of(Path.GetDirectoryName(folder)!) is not { } alias)
        {
            return;
        }

        var reached = At(Path.Combine(alias, "tool"));

        Assert.Contains(Path.Combine(alias, "tool"), reached.Places, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(ReachedFolder.Comparable(folder), reached.Places, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("secret", reached.PathTo(Path.Combine(alias, "tool", "secret")));
        Assert.Null(reached.PathTo(Path.Combine(alias, "beside", "secret")));
    }

    /// <summary>
    /// A folder reached through an alias is named below another the way that other is named, so a
    /// rule asked of text sees it where it is. One beside it is named nowhere (§5.6).
    /// </summary>
    [Fact]
    public void NamesAFolderReachedThroughAnAliasBelowAnother()
    {
        _volumes.Substituting(@"S:\", @"C:\Source");

        Assert.Equal(@"C:\Source\app\src", At(@"C:\Source").Naming(At(@"S:\app\src"), @"C:\Source"));
        Assert.Equal(@"C:\Source", At(@"C:\Source").Naming(At(@"S:\"), @"C:\Source"));
        Assert.Equal(@"S:\app", At(@"S:\").Naming(At(@"C:\Source\app"), @"S:\"));
        Assert.Null(At(@"C:\Source").Naming(At(@"C:\Elsewhere\app"), @"C:\Source"));
    }
}
