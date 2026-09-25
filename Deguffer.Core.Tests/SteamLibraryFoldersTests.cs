using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Steam's list of its game libraries. Every library it names is a place a deletion is planned, so
/// a path read wrongly, counted twice or resolved against the wrong directory is the failure.
/// </summary>
public sealed class SteamLibraryFoldersTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Install => Path.Combine(_temp.Path, "Steam");

    private void WriteList(string text)
    {
        var steamapps = Path.Combine(Install, "steamapps");
        Directory.CreateDirectory(steamapps);
        File.WriteAllText(Path.Combine(steamapps, SteamLibraryFolders.ListFileName), text);
    }

    private static string Escaped(string path) => path.Replace(@"\", @"\\");

    [Fact]
    public void AMissingListIsACompleteAnswerOfOneLibrary()
    {
        Directory.CreateDirectory(Install);

        var libraries = SteamLibraryFolders.Of(Install);

        Assert.Equal(new[] { Install }, libraries.Folders);
        Assert.Equal(SteamLibraryListing.Absent, libraries.Listing);
        Assert.True(libraries.IsComplete);
    }

    /// <summary>
    /// Steam lists the install directory as library "0", and writes it in its own case. It is one
    /// library, not two, or its caches would be planned twice.
    /// </summary>
    [Fact]
    public void TheInstallIsNotCountedTwiceWhateverCaseOrSeparatorTheListUses()
    {
        var second = Path.Combine(_temp.Path, "Library");

        WriteList($$"""
            "libraryfolders"
            {
                "0" { "path" "{{Escaped(Install.ToUpperInvariant())}}\\" }
                "1" { "path" "{{Escaped(second)}}" }
                "2" { "path" "{{Escaped(second)}}" }
            }
            """);

        var libraries = SteamLibraryFolders.Of(Install);

        Assert.Equal(new[] { Install, second }, libraries.Folders);
        Assert.Equal(SteamLibraryListing.Read, libraries.Listing);
    }

    /// <summary>
    /// A relative path would resolve against Deguffer's own working directory, which no library is
    /// in, and an entry with no path names nowhere. Neither is followed, the libraries beside them are
    /// still read, and the list stops claiming to be every library — a library Steam named is never
    /// lost in silence.
    /// </summary>
    [Theory]
    [InlineData("\"1\" { \"path\" \"SteamLibrary\" }")]
    [InlineData("\"1\" { \"label\" \"\" }")]
    public void ALibraryThatCannotBePlacedIsNotFollowedAndMakesTheListIncomplete(string entry)
    {
        var second = Path.Combine(_temp.Path, "Library");

        WriteList($$"""
            "libraryfolders"
            {
                {{entry}}
                "2" { "path" "{{Escaped(second)}}" }
            }
            """);

        var libraries = SteamLibraryFolders.Of(Install);

        Assert.Equal(new[] { Install, second }, libraries.Folders);
        Assert.Equal(SteamLibraryListing.Unplaced, libraries.Listing);
        Assert.False(libraries.IsComplete);
    }

    /// <summary>
    /// A list past any size Steam writes is not read, and is not called absent either: something is
    /// there and nothing established what it says.
    /// </summary>
    [Fact]
    public void AListLargerThanSteamWritesIsUnreadAndIncomplete()
    {
        WriteList("\"libraryfolders\" {" + new string(' ', 3 * 1024 * 1024) + "}");

        var libraries = SteamLibraryFolders.Of(Install);

        Assert.Equal(new[] { Install }, libraries.Folders);
        Assert.Equal(SteamLibraryListing.Unreadable, libraries.Listing);
        Assert.False(libraries.IsComplete);
    }

    [Theory]
    [InlineData("not a list")]
    [InlineData(@"""somethingelse"" { ""1"" { ""path"" ""D:\\SteamLibrary"" } }")]
    [InlineData("\"libraryfolders\" \"a value, not a block\"")]
    public void AListThatIsNotALibraryListIsMalformedAndOnlyTheInstallIsKnown(string text)
    {
        WriteList(text);

        var libraries = SteamLibraryFolders.Of(Install);

        Assert.Equal(new[] { Install }, libraries.Folders);
        Assert.Equal(SteamLibraryListing.Malformed, libraries.Listing);
        Assert.False(libraries.IsComplete);
    }
}
