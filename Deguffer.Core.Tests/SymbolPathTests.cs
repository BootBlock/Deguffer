using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// A symbol path read as Microsoft's "Advanced SymSrv Use" page describes it: which folders debuggers copy
/// symbols into, where those symbols come from, and which folders are somebody's own store. The row a
/// store is offered on, and whether it is offered at all, follows from these answers.
/// </summary>
public sealed class SymbolPathTests
{
    private const string Microsoft = SymbolPath.MicrosoftServer;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ; ;")]
    public void ReadsNothingFromAnEmptyPath(string? value)
    {
        var reading = SymbolPath.Read(value);

        Assert.Empty(reading.Stores);
        Assert.Empty(reading.Masters);
    }

    [Theory]
    [InlineData(@"srv*C:\Symbols*https://msdl.microsoft.com/download/symbols")]
    [InlineData(@"SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols")]
    [InlineData(@"symsrv*symsrv.dll*C:\Symbols*https://msdl.microsoft.com/download/symbols")]
    [InlineData(@" ""srv*C:\Symbols*https://msdl.microsoft.com/download/symbols"" ")]
    public void ReadsTheDownstreamStoreOfAChain(string value)
    {
        var store = Assert.Single(SymbolPath.Read(value).Stores);

        Assert.Equal(@"C:\Symbols", store.Folder);
        Assert.Equal([Microsoft], store.Sources);
    }

    /// <summary>
    /// Every store left of the last is a downstream store filled from the last, which is somebody's own
    /// store and never a copy.
    /// </summary>
    [Fact]
    public void ReadsEveryDownstreamStoreAndTheMasterOfALongerChain()
    {
        var reading = SymbolPath.Read(@"srv*C:\Near*D:\Far*\\builds\symbols");

        Assert.Equal([@"C:\Near", @"D:\Far"], reading.Stores.Select(store => store.Folder));
        Assert.All(reading.Stores, store => Assert.Equal([@"\\builds\symbols"], store.Sources));
        Assert.Equal([@"\\builds\symbols"], reading.Masters);
    }

    /// <summary>An empty store, two asterisks in a row or one at the end, is the default store.</summary>
    [Theory]
    [InlineData("srv**https://msdl.microsoft.com/download/symbols")]
    [InlineData("srv*https://msdl.microsoft.com/download/symbols")]
    [InlineData("srv*")]
    public void ReadsTheDefaultStore(string value)
    {
        var store = Assert.Single(SymbolPath.Read(value).Stores);

        Assert.Null(store.Folder);
        Assert.Equal([Microsoft], store.Sources);
    }

    /// <summary>A chain of one share copies nothing: the debugger reads the share where it is.</summary>
    [Fact]
    public void ReadsNoStoreFromAChainOfOneShare()
    {
        var reading = SymbolPath.Read(@"srv*\\builds\symbols");

        Assert.Empty(reading.Stores);
        Assert.Equal([@"\\builds\symbols"], reading.Masters);
    }

    /// <summary>
    /// A cache element keeps a copy of what every element to its right loads, a plain folder included,
    /// and nothing of what is to its left.
    /// </summary>
    [Fact]
    public void ReadsACacheElementAsFilledFromEverythingToItsRight()
    {
        var reading = SymbolPath.Read(@"C:\Before;cache*C:\Cache;srv*https://symbols.nuget.org/download/symbols;D:\Builds");

        var cache = Assert.Single(reading.Stores, store => store.Folder == @"C:\Cache");

        Assert.Equal(["D:\\Builds", "https://symbols.nuget.org/download/symbols"], cache.Sources.Order(StringComparer.Ordinal));
        Assert.Equal([@"C:\Before", @"D:\Builds"], reading.Masters);
    }

    [Fact]
    public void ReadsAnEmptyCacheElementAsTheDefaultStore()
    {
        var store = Assert.Single(SymbolPath.Read(@"cache*;D:\Builds").Stores);

        Assert.Null(store.Folder);
        Assert.Equal([@"D:\Builds"], store.Sources);
    }

    /// <summary>A cache element with nothing to its right has nothing to copy, so it makes no store.</summary>
    [Fact]
    public void ReadsNoStoreFromACacheElementAtTheEnd()
    {
        Assert.DoesNotContain(SymbolPath.Read(@"srv*C:\Symbols*\\builds\symbols;cache*C:\Cache").Stores, store => store.Folder == @"C:\Cache");
    }

    [Theory]
    [InlineData("https://msdl.microsoft.com/download/symbols", true)]
    [InlineData("http://msdl.microsoft.com/download/symbols", true)]
    [InlineData("https://MSDL.microsoft.com/download/symbols", true)]
    [InlineData("https://symbols.nuget.org/download/symbols", true)]
    [InlineData("https://symbols.example.com/download/symbols", false)]
    [InlineData("https://msdl.microsoft.com.example.com/symbols", false)]
    [InlineData(@"\\msdl.microsoft.com\symbols", false)]
    [InlineData(@"C:\Symbols", false)]
    public void TellsMicrosoftsPublicServersFromEveryOtherSource(string source, bool microsoft)
    {
        Assert.Equal(microsoft, SymbolStoreLayout.IsMicrosoftServer(source));
    }
}
