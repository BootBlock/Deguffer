using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;
using static Deguffer.Core.Tests.Fakes.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a conversation says about itself, read from its head and its tail. Every transcript here is
/// invented.
/// </summary>
public sealed class ClaudeCodeTranscriptReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ClaudeCodeFixture _claude;

    public ClaudeCodeTranscriptReaderTests() => _claude = new ClaudeCodeFixture(Path.Combine(_temp.Path, ".claude"));

    public void Dispose() => _temp.Dispose();

    private string Write(string content) => _claude.WriteText(Path.Combine(_claude.Project(), SessionA + ".jsonl"), content);

    [Fact]
    public void ReadsTheProjectTheStartAndTheTitle()
    {
        var started = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var path = _claude.Conversation(SessionA, started: started);

        var conversation = ClaudeCodeTranscriptReader.Read(path, CancellationToken.None);

        Assert.Equal(new ClaudeCodeConversation(ProjectPath, started, "Tidy the build scripts"), conversation);
    }

    /// <summary>The last line of a live transcript can be half written. It is not a line, and the rest still reads.</summary>
    [Fact]
    public void AHalfWrittenLastLineIsIgnored()
    {
        var path = Write(
            "{\"type\":\"user\",\"cwd\":\"C:\\\\p\",\"timestamp\":\"2026-07-01T12:00:00Z\"}\n"
            + "{\"type\":\"ai-title\",\"aiTitle\":\"Whole\"}\n"
            + "{\"type\":\"ai-title\",\"aiTi");

        Assert.Equal("Whole", ClaudeCodeTranscriptReader.Read(path, CancellationToken.None)!.Title);
    }

    /// <summary>A last line with no line break after it is still a line.</summary>
    [Fact]
    public void ALastLineWithNoLineBreakIsRead()
    {
        var path = Write("{\"type\":\"user\",\"cwd\":\"C:\\\\p\"}");

        Assert.Equal(@"C:\p", ClaudeCodeTranscriptReader.Read(path, CancellationToken.None)!.Project);
    }

    /// <summary>
    /// The head is read no further than its budget. A folder recorded only past it is never found, so the
    /// conversation is not described rather than read whole.
    /// </summary>
    [Fact]
    public void NothingPastTheHeadsBudgetIsRead()
    {
        var path = Write(
            "{\"type\":\"attachment\",\"content\":\"" + new string('x', 2 * 1024 * 1024) + "\"}\n"
            + "{\"type\":\"user\",\"cwd\":\"C:\\\\p\"}\n"
            + new string('\n', 70 * 1024));

        Assert.Null(ClaudeCodeTranscriptReader.Read(path, CancellationToken.None));
    }

    /// <summary>A line that mentions a title in somebody's words is not a title.</summary>
    [Fact]
    public void ATitleIsTakenOnlyFromATitleLine()
    {
        var path = Write(
            "{\"type\":\"user\",\"cwd\":\"C:\\\\p\",\"message\":{\"content\":\"{\\\"type\\\":\\\"ai-title\\\",\\\"aiTitle\\\":\\\"Quoted\\\"}\"}}\n"
            + "{\"type\":\"last-prompt\",\"lastPrompt\":\"ai-title custom-title\"}\n");

        Assert.Null(ClaudeCodeTranscriptReader.Read(path, CancellationToken.None)!.Title);
    }

    [Fact]
    public void ATitleIsShownOnOneLineAndCutToLength()
    {
        var path = Write(
            "{\"type\":\"user\",\"cwd\":\"C:\\\\p\"}\n"
            + "{\"type\":\"ai-title\",\"aiTitle\":\"  Two\\nlines\\t\\there " + new string('y', 300) + "\"}\n");

        var title = ClaudeCodeTranscriptReader.Read(path, CancellationToken.None)!.Title!;

        Assert.StartsWith("Two lines here yyy", title, StringComparison.Ordinal);
        Assert.Equal(200, title.Length);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }

    /// <summary>
    /// A field holding an unpaired surrogate parses, and cannot be read as a string. The conversation is not
    /// described, rather than stopping the scan.
    /// </summary>
    [Theory]
    [InlineData("cwd")]
    [InlineData("aiTitle")]
    public void AFieldThatCannotBeReadAsAStringLeavesTheConversationUndescribed(string field)
    {
        var path = Write(
            "{\"type\":\"user\",\"cwd\":" + (field == "cwd" ? "\"\\ud800\"" : "\"C:\\\\p\"") + "}\n"
            + "{\"type\":\"ai-title\",\"aiTitle\":" + (field == "aiTitle" ? "\"\\ud800\"" : "\"Fine\"") + "}\n");

        Assert.Null(ClaudeCodeTranscriptReader.Read(path, CancellationToken.None));
    }

    /// <summary>A tail that starts exactly where a line starts keeps that line.</summary>
    [Fact]
    public void ATitleLineStartingExactlyWhereTheTailStartsIsRead()
    {
        var title = "{\"type\":\"ai-title\",\"aiTitle\":\"Edge\"}\n";
        var head = "{\"type\":\"user\",\"cwd\":\"C:\\\\p\"}\n";
        var filler = new string('x', 2 * 1024 * 1024) + "\n";
        var tail = title + new string(' ', 64 * 1024 - title.Length - 1) + "\n";

        var path = Write(head + filler + tail);

        Assert.Equal("Edge", ClaudeCodeTranscriptReader.Read(path, CancellationToken.None)!.Title);
    }

    [Fact]
    public void AMissingFileIsNotDescribed() =>
        Assert.Null(ClaudeCodeTranscriptReader.Read(Path.Combine(_temp.Path, "gone.jsonl"), CancellationToken.None));
}
