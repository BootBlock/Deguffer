using Deguffer.Core.Startup;

namespace Deguffer.Core.Tests;

/// <summary>
/// What Memory says about a picked program Windows starts at sign-in (§7.5): a fact and a link to the
/// entry, matched by full path, and nothing for a program no entry starts.
/// </summary>
public sealed class StartupLinkTests
{
    private const string Tool = @"C:\Program Files\Tool\tool.exe";

    [Fact]
    public void AProgramAnEntryStartsLinksToThatEntry()
    {
        var entry = Entry("Tool", StartupState.Starts);

        var link = StartupLink.For(Tool, new StartupReading([entry], []));

        Assert.Equal(entry, link?.Entry);
        Assert.StartsWith("Windows starts this program at sign-in", link!.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisabledEntryIsSaidToBeDisabledAndStillLinked()
    {
        var link = StartupLink.For(Tool, new StartupReading([Entry("Tool", StartupState.Disabled)], []));

        Assert.Contains("disabled", link!.Sentence, StringComparison.Ordinal);
    }

    /// <summary>The image path Windows gives can carry the long-path prefix, which names the same file.</summary>
    [Fact]
    public void TheLongPathFormOfTheProgramIsTheSameProgram() =>
        Assert.NotNull(StartupLink.For(@"\\?\" + Tool.ToUpperInvariant(), new StartupReading([Entry("Tool", StartupState.Starts)], [])));

    [Fact]
    public void AProgramNoEntryStartsHasNoLink()
    {
        Assert.Null(StartupLink.For(@"C:\Other\other.exe", new StartupReading([Entry("Tool", StartupState.Starts)], [])));
        Assert.Null(StartupLink.For(null, new StartupReading([Entry("Tool", StartupState.Starts)], [])));
    }

    /// <summary>
    /// Memory recommends nothing (§7.2), and neither does its link (§7.5): every sentence it can give
    /// states what Windows does, and none suggests changing it.
    /// </summary>
    [Theory]
    [InlineData(StartupState.Starts)]
    [InlineData(StartupState.Disabled)]
    [InlineData(StartupState.Unknown)]
    public void TheLinkNeverSuggestsAChange(StartupState state)
    {
        var sentence = StartupLink.For(Tool, new StartupReading([Entry("Tool", state)], []))!.Sentence;

        Assert.DoesNotContain("should", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disable it", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe", sentence, StringComparison.OrdinalIgnoreCase);
    }

    private static StartupEntry Entry(string name, StartupState state) => new(
        new StartupKey(StartupSource.UserRun, name), name, Tool, Tool, ProgramStanding.Present, null, state, Approval.None);
}
