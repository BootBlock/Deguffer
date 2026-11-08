using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a candidate directory is spelt for git. The forms here are the ones the real-git tests in
/// <see cref="TrackedFileCheckGitTests"/> prove git matches as Windows would.
/// </summary>
public sealed class GitPathspecTests
{
    /// <summary>
    /// A path in ASCII is asked about as itself, ignoring case. A glob character in it is part of the
    /// name, not a pattern over other directories.
    /// </summary>
    [Theory]
    [InlineData("src/App/obj", ":(literal,icase)src/App/obj")]
    [InlineData("App[Legacy]/obj", ":(literal,icase)App[Legacy]/obj")]
    public void AsksAboutAnAsciiPathLiterallyWithoutRegardToCase(string path, string expected) =>
        Assert.Equal(expected, GitPathspec.For(path));

    /// <summary>
    /// Git's <c>icase</c> folds ASCII letters only, so a literal pathspec for <c>Übung</c> does not
    /// find an index that spells it <c>übung</c>. Each run of non-ASCII characters becomes one
    /// <c>*</c>, which cannot cross a <c>/</c>, and the directory's contents are matched with
    /// <c>/**</c> because a glob names a path rather than a directory.
    /// </summary>
    [Theory]
    [InlineData("Übung/obj", ":(glob,icase)*bung/obj/**")]
    [InlineData("src/Ärger und Öl/obj", ":(glob,icase)src/*rger und *l/obj/**")]
    [InlineData("项目/obj", ":(glob,icase)*/obj/**")]
    [InlineData("App𐐀Two/obj", ":(glob,icase)App*Two/obj/**")]
    public void AsksAboutAPathOutsideAsciiWithAGlobThatStandsInForItsOtherLetters(string path, string expected) =>
        Assert.Equal(expected, GitPathspec.For(path));

    /// <summary>
    /// Once the path is a glob, a glob character in the name must be matched as itself, or
    /// <c>Ä[b]</c> would ask about <c>Äb</c>. A backslash cannot escape it: git for Windows reads a
    /// backslash in a pathspec as a separator.
    /// </summary>
    [Fact]
    public void MatchesAGlobCharacterInANameOutsideAsciiAsItself() =>
        Assert.Equal(":(glob,icase)*[[]b[]]x/obj/**", GitPathspec.For("Ä[b]x/obj"));
}
