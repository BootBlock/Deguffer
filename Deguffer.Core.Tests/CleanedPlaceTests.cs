using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a place a Storage clean deletes in holds (§7.4): a whole folder holds everything in it, and a
/// place found by name holds only what is inside a folder with one of the names.
/// </summary>
public sealed class CleanedPlaceTests
{
    private const string Source = @"C:\Users\testuser\Source";

    [Theory]
    [InlineData(@"C:\Users\testuser\AppData\Local\npm-cache", true)]
    [InlineData(@"C:\Users\testuser\AppData\Local\npm-cache\_cacache\a.tgz", true)]
    [InlineData(@"c:\users\TESTUSER\appdata\local\NPM-CACHE\a.tgz", true)]
    [InlineData(@"\\?\C:\Users\testuser\AppData\Local\npm-cache\a.tgz", true)]
    [InlineData(@"C:\Users\testuser\AppData\Local\npm-cache-old\a.tgz", false)]
    [InlineData(@"C:\Users\testuser\AppData\Local\a.tgz", false)]
    public void AWholePlaceHoldsItselfAndEverythingInIt(string path, bool held) =>
        Assert.Equal(held, CleanedPlace.Whole(@"C:\Users\testuser\AppData\Local\npm-cache").Holds(path));

    [Theory]
    [InlineData(@"C:\Users\testuser\Source\app\node_modules", true)]
    [InlineData(@"C:\Users\testuser\Source\app\node_modules\x\index.js", true)]
    [InlineData(@"C:\Users\testuser\Source\deep\er\app\NODE_MODULES\x.js", true)]
    [InlineData(@"C:\Users\testuser\Source\app\src\index.js", false)]
    [InlineData(@"C:\Users\testuser\Source", false)]
    [InlineData(@"C:\Users\testuser\Other\app\node_modules\x.js", false)]
    public void APlaceFoundByNameHoldsOnlyWhatIsInAFolderWithTheName(string path, bool held) =>
        Assert.Equal(held, CleanedPlace.FoldersNamed(Source, ["node_modules"]).Holds(path));

    [Fact]
    public void APlaceFoundByNameIsNotHeldByTheNameOfItsTop() =>
        Assert.False(CleanedPlace.FoldersNamed(@"C:\Users\testuser\node_modules", ["node_modules"]).Holds(@"C:\Users\testuser\node_modules\a.js"));

    [Fact]
    public void APlaceFoundByNameNeedsAName() =>
        Assert.Throws<ArgumentException>(() => CleanedPlace.FoldersNamed(Source, []));
}
