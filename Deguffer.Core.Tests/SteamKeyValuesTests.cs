using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// Valve's key-values text, which names the libraries a deletion is planned in. The failures that
/// matter are a path read wrongly and a broken file read as a shorter one.
/// </summary>
public sealed class SteamKeyValuesTests
{
    [Fact]
    public void ReadsNestedBlocksAndDoubledBackslashes()
    {
        var entries = SteamKeyValues.Parse("""
            "libraryfolders"
            {
                "0"
                {
                    "path"		"D:\\Steam Library\\Games"
                    "apps"
                    {
                        "440"		"1000"
                    }
                }
            }
            """);

        var root = Assert.Single(entries!);
        var library = Assert.Single(root.Children);

        Assert.Equal("0", library.Key);
        Assert.Equal(@"D:\Steam Library\Games", library.Child("path")!.Value);
        Assert.Equal("1000", library.Child("apps")!.Child("440")!.Value);
    }

    /// <summary>
    /// A backslash that is not an escape is kept, so a path in a file somebody edited by hand still
    /// names the folder it was written as.
    /// </summary>
    [Fact]
    public void KeepsABackslashThatIsNotAnEscape()
    {
        var entries = SteamKeyValues.Parse(@"""path"" ""D:\SteamLibrary""");

        Assert.Equal(@"D:\SteamLibrary", Assert.Single(entries!).Value);
    }

    [Fact]
    public void ReadsEscapedQuotesCommentsConditionsAndUnquotedTokens()
    {
        var entries = SteamKeyValues.Parse("""
            // a comment
            root
            {
                "name"  "A \"quoted\" name" [$WIN32]
                count   3 // trailing comment
            }
            """);

        var root = Assert.Single(entries!);

        Assert.Equal("A \"quoted\" name", root.Child("name")!.Value);
        Assert.Equal("3", root.Child("COUNT")!.Value);
    }

    [Theory]
    [InlineData("\"key\" \"unterminated")]
    [InlineData("\"key\"")]
    [InlineData("\"key\" { \"inner\" \"value\"")]
    [InlineData("\"key\" \"value\" }")]
    [InlineData("{ \"key\" \"value\" }")]
    [InlineData("\"key\" [$WIN32")]
    public void AFileItCannotReadWhollyIsNotReadAtAll(string text) => Assert.Null(SteamKeyValues.Parse(text));

    /// <summary>
    /// Nesting past anything Steam writes is refused rather than followed, so a hostile file cannot
    /// exhaust the stack.
    /// </summary>
    [Fact]
    public void RefusesNestingPastAnythingSteamWrites()
    {
        const int depth = 10_000;
        var text = string.Concat(Enumerable.Repeat("\"k\" {", depth)) + new string('}', depth);

        Assert.Null(SteamKeyValues.Parse(text));
    }

    [Fact]
    public void AcceptsNestingAsDeepAsSteamWrites()
    {
        const int depth = 8;
        var text = string.Concat(Enumerable.Repeat("\"k\" {", depth)) + new string('}', depth);

        Assert.NotNull(SteamKeyValues.Parse(text));
    }
}
