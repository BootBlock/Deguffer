using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// What npm prints for <c>npm config get cache userconfig globalconfig prefix</c>: one
/// <c>key=value</c> line per key, read for the cache and the three paths npm reads its configuration
/// through.
/// </summary>
public sealed class NpmLocationsTests
{
    private const string Default = @"C:\Users\testuser\AppData\Local\npm-cache";

    [Fact]
    public void ReadsEachKeyFromItsOwnLine()
    {
        var read = Read(
            "cache=D:\\caches\\npm\r\n"
            + "userconfig=C:\\Users\\testuser\\.npmrc\r\n"
            + "globalconfig=C:\\Users\\testuser\\AppData\\Roaming\\npm\\etc\\npmrc\r\n"
            + "prefix=C:\\Users\\testuser\\AppData\\Roaming\\npm\r\n");

        Assert.Equal(new AskedLocation(@"D:\caches\npm", Answered: true), read.Cache);
        Assert.Equal(@"C:\Users\testuser\.npmrc", read.UserConfig);
        Assert.Equal(@"C:\Users\testuser\AppData\Roaming\npm\etc\npmrc", read.GlobalConfig);
        Assert.Equal(@"C:\Users\testuser\AppData\Roaming\npm", read.Prefix);
        Assert.True(read.IsComplete);
    }

    [Fact]
    public void ComparesKeysWithoutCaseAndTrimsWhatSurroundsThem()
    {
        var read = Read(
            "  Cache = D:\\caches\\npm \n"
            + "USERCONFIG=C:\\Users\\testuser\\.npmrc\n"
            + "GlobalConfig =C:\\Users\\testuser\\etc\\npmrc\n"
            + "Prefix= C:\\Users\\testuser\\npm\n");

        Assert.Equal(@"D:\caches\npm", read.Cache.Path);
        Assert.Equal(@"C:\Users\testuser\.npmrc", read.UserConfig);
        Assert.Equal(@"C:\Users\testuser\etc\npmrc", read.GlobalConfig);
        Assert.Equal(@"C:\Users\testuser\npm", read.Prefix);
        Assert.True(read.IsComplete);
    }

    /// <summary>A folder may hold an equals sign: only the first one on a line ends the key.</summary>
    [Fact]
    public void SplitsALineAtItsFirstEqualsSign()
    {
        var read = Read("cache=D:\\a=b\\npm-cache\nuserconfig=C:\\x=y\\.npmrc\nglobalconfig=C:\\g\\npmrc\nprefix=C:\\p\n");

        Assert.Equal(@"D:\a=b\npm-cache", read.Cache.Path);
        Assert.Equal(@"C:\x=y\.npmrc", read.UserConfig);
    }

    /// <summary>
    /// A path that is not a full path names no file anyone can stamp, so the answer is planned from
    /// but is not complete, and is never remembered.
    /// </summary>
    [Fact]
    public void KeepsOnlyFullPaths()
    {
        var read = Read("cache=D:\\caches\\npm\nuserconfig=.npmrc\nglobalconfig=C:\\g\\npmrc\nprefix=C:\\p\n");

        Assert.Equal(@"D:\caches\npm", read.Cache.Path);
        Assert.Null(read.UserConfig);
        Assert.False(read.IsComplete);
    }

    [Fact]
    public void IsIncompleteWhereAKeyIsMissing()
    {
        var read = Read("cache=D:\\caches\\npm\nuserconfig=C:\\u\\.npmrc\nglobalconfig=C:\\g\\npmrc\n");

        Assert.True(read.Cache.Answered);
        Assert.Null(read.Prefix);
        Assert.False(read.IsComplete);
    }

    /// <summary>npm prints a single key as the bare value, which is read as before.</summary>
    [Fact]
    public void ReadsTheLastRootedLineWhereNoLineNamesTheCache()
    {
        var read = Read("npm warn something\nD:\\caches\\npm\n");

        Assert.Equal(new AskedLocation(@"D:\caches\npm", Answered: true), read.Cache);
        Assert.False(read.IsComplete);
    }

    [Fact]
    public void FallsBackToTheDefaultWhereNothingRootedWasPrinted()
    {
        var read = Read("cache=relative\\npm-cache\n");

        Assert.Equal(new AskedLocation(Default, Answered: false), read.Cache);
        Assert.False(read.IsComplete);
    }

    /// <summary>A failed query is no answer, whatever it printed, for the cache and the paths alike.</summary>
    [Fact]
    public void NamesNothingWhereTheQueryFailed()
    {
        var read = NpmLocations.From(
            new CommandOutcome(1, "cache=D:\\caches\\npm\nuserconfig=C:\\u\\.npmrc\nglobalconfig=C:\\g\\npmrc\nprefix=C:\\p\n", string.Empty),
            Default);

        Assert.Equal(new AskedLocation(Default, Answered: false), read.Cache);
        Assert.Null(read.UserConfig);
        Assert.Null(read.GlobalConfig);
        Assert.Null(read.Prefix);
        Assert.False(read.IsComplete);
    }

    private static NpmLocations Read(string output) => NpmLocations.From(new CommandOutcome(0, output, string.Empty), Default);
}
