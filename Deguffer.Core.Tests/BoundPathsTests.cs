using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The paths a removal holds back, filed under the folder each sits in. A walk asks by the string its
/// listing spells a folder with and by the name the listing read, so every way a caller may write a
/// bound path has to land where that question finds it, or the entry is deleted (§5.2).
/// </summary>
public sealed class BoundPathsTests
{
    [Theory]
    [InlineData(@"C:\Temp\session")]
    [InlineData(@"C:\Temp\session\")]
    [InlineData(@"C:\TEMP\Session")]
    [InlineData(@"\\?\C:\Temp\session")]
    [InlineData(@"\\?\C:\Temp\session\")]
    public void FindsABoundPathHoweverItIsWritten(string written)
    {
        var paths = new BoundPaths([], [written], []);

        Assert.Equal(BoundReason.Spared, paths.In(@"\\?\C:\Temp")?.Of("session"));
        Assert.Equal(BoundReason.Spared, paths.In(@"\\?\C:\Temp\")?.Of("SESSION"));
        Assert.Equal(BoundReason.Spared, paths.Of(@"\\?\C:\Temp\session"));
    }

    /// <summary>
    /// The listing spells a volume's top with its separator, and the folder cut from a path directly
    /// below it has none.
    /// </summary>
    [Fact]
    public void FilesAPathDirectlyBelowAVolumesTopUnderTheTop()
    {
        var paths = new BoundPaths([@"C:\notes"], [], []);

        Assert.Equal(BoundReason.UserWork, paths.In(@"\\?\C:\")?.Of("notes"));
        Assert.Equal(BoundReason.UserWork, paths.Of(@"\\?\C:\notes"));
    }

    [Fact]
    public void FilesAPathDirectlyBelowAShareUnderTheShare()
    {
        var paths = new BoundPaths([], [], [@"\\server.test\share\other-rows"]);

        Assert.Equal(BoundReason.OwnedElsewhere, paths.In(@"\\?\UNC\server.test\share")?.Of("other-rows"));
        Assert.Equal(BoundReason.OwnedElsewhere, paths.In(@"\\?\UNC\server.test\share\")?.Of("other-rows"));
    }

    /// <summary>
    /// Nothing a walk lists is a volume's top, so a bound naming one holds nothing back, as it did when
    /// each entry's full path was looked up.
    /// </summary>
    [Fact]
    public void ABoundNamingAVolumesTopHoldsNothingBack()
    {
        var paths = new BoundPaths([], [@"C:\", @"\\server.test\share"], []);

        Assert.True(paths.IsEmpty);
        Assert.Equal(BoundReason.None, paths.Of(@"\\?\C:\"));
    }

    /// <summary>
    /// User work is the reason that outlasts the others, so a path given for two reasons is reported as
    /// user work, as the walk asked it first when each reason was a set of its own.
    /// </summary>
    [Fact]
    public void APathGivenForTwoReasonsIsHeldBackForTheFirst()
    {
        var paths = new BoundPaths([@"C:\Temp\sketch"], [@"C:\Temp\sketch", @"C:\Temp\live"], [@"C:\Temp\live"]);

        Assert.Equal(BoundReason.UserWork, paths.Of(@"\\?\C:\Temp\sketch"));
        Assert.Equal(BoundReason.Spared, paths.Of(@"\\?\C:\Temp\live"));
    }

    /// <summary>A name or a folder that only begins like a bound one is not held back.</summary>
    [Fact]
    public void HoldsBackOnlyTheEntryNamed()
    {
        var paths = new BoundPaths([], [@"C:\Temp\session"], []);

        Assert.Null(paths.In(@"\\?\C:\Tem"));
        Assert.Null(paths.In(@"\\?\C:\Temp\session"));
        Assert.Equal(BoundReason.None, paths.In(@"\\?\C:\Temp")?.Of("sessio"));
        Assert.Equal(BoundReason.None, paths.In(@"\\?\C:\Temp")?.Of("session-2"));
        Assert.Equal(BoundReason.None, paths.Of(@"\\?\C:\Temp\session\inside"));
    }
}
