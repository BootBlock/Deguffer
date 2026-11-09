using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using static Deguffer.Testing.ClaudeCodeFixture;

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

    /// <summary>A conversation as a plan comes to know it: placed, then described.</summary>
    private static ClaudeCodeConversation? Read(string path)
    {
        var reader = ClaudeCodeTranscriptReader.Default;

        return reader.Place(path, CancellationToken.None) is { } placement
            ? reader.Describe(path, placement, CancellationToken.None)
            : null;
    }

    /// <summary>Placing a conversation reads its first lines and never goes near the rest.</summary>
    [Fact]
    public void PlacingAConversationReadsOnlyItsFirstLines()
    {
        var reads = new RecordedTranscriptReads();
        var started = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var path = _claude.Conversation(SessionA, started: started, lateTitle: "Later", padding: 300 * 1024);

        var placement = reads.Reader.Place(path, CancellationToken.None);

        Assert.Equal(new ClaudeCodePlacement(ProjectPath, started), placement);
        Assert.InRange(reads.Furthest, 1, 4 * 1024);
    }

    /// <summary>The first read is small and the budget is not: a folder recorded far in is still found.</summary>
    [Theory]
    [InlineData(10 * 1024)]
    [InlineData(100 * 1024)]
    [InlineData(900 * 1024)]
    public void PlacingAConversationReadsOnUntilItFindsTheFolder(int before)
    {
        var path = Write(
            "{\"type\":\"attachment\",\"timestamp\":\"2026-07-01T12:00:00Z\",\"content\":\"" + new string('x', before) + "\"}\n"
            + "{\"type\":\"user\",\"cwd\":\"C:\\\\p\"}\n");

        Assert.Equal(@"C:\p", ClaudeCodeTranscriptReader.Default.Place(path, CancellationToken.None)!.Project);
    }

    /// <summary>A title is read from the tail, so describing one reaches the end of the file.</summary>
    [Fact]
    public void DescribingAConversationTakesTheTitleFromItsTail()
    {
        var reads = new RecordedTranscriptReads();
        var path = _claude.Conversation(SessionA, lateTitle: "Later", padding: 300 * 1024);
        var placement = reads.Reader.Place(path, CancellationToken.None)!;

        var conversation = reads.Reader.Describe(path, placement, CancellationToken.None);

        Assert.Equal("Later", conversation!.Title);
        Assert.Equal(new FileInfo(path).Length, reads.Furthest);
    }

    /// <summary>A transcript can go between the two reads. It is then not described, and so not offered.</summary>
    [Fact]
    public void AConversationGoneBetweenBeingPlacedAndDescribedIsNotDescribed()
    {
        var path = _claude.Conversation(SessionA);
        var placement = ClaudeCodeTranscriptReader.Default.Place(path, CancellationToken.None)!;

        File.Delete(path);

        Assert.Null(ClaudeCodeTranscriptReader.Default.Describe(path, placement, CancellationToken.None));
    }

    /// <summary>§6.3: a truncated path here would read as a conversation that cannot be placed.</summary>
    [Fact]
    public void EveryTranscriptIsOpenedByItsExtendedPath()
    {
        var reads = new RecordedTranscriptReads();
        var path = _claude.Conversation(SessionA);
        var placement = reads.Reader.Place(path, CancellationToken.None)!;

        reads.Reader.Describe(path, placement, CancellationToken.None);

        Assert.Equal(2, reads.Opened.Count);
        Assert.All(reads.Opened, opened => Assert.Equal(LongPath.Extended(path), opened));
        Assert.All(reads.Opened, opened => Assert.StartsWith(@"\\?\", opened, StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsTheProjectTheStartAndTheTitle()
    {
        var started = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var path = _claude.Conversation(SessionA, started: started);

        var conversation = Read(path);

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

        Assert.Equal("Whole", Read(path)!.Title);
    }

    /// <summary>A last line with no line break after it is still a line.</summary>
    [Fact]
    public void ALastLineWithNoLineBreakIsRead()
    {
        var path = Write("{\"type\":\"user\",\"cwd\":\"C:\\\\p\"}");

        Assert.Equal(@"C:\p", Read(path)!.Project);
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

        Assert.Null(Read(path));
    }

    /// <summary>A line that mentions a title in somebody's words is not a title.</summary>
    [Fact]
    public void ATitleIsTakenOnlyFromATitleLine()
    {
        var path = Write(
            "{\"type\":\"user\",\"cwd\":\"C:\\\\p\",\"message\":{\"content\":\"{\\\"type\\\":\\\"ai-title\\\",\\\"aiTitle\\\":\\\"Quoted\\\"}\"}}\n"
            + "{\"type\":\"last-prompt\",\"lastPrompt\":\"ai-title custom-title\"}\n");

        Assert.Null(Read(path)!.Title);
    }

    [Fact]
    public void ATitleIsShownOnOneLineAndCutToLength()
    {
        var path = Write(
            "{\"type\":\"user\",\"cwd\":\"C:\\\\p\"}\n"
            + "{\"type\":\"ai-title\",\"aiTitle\":\"  Two\\nlines\\t\\there " + new string('y', 300) + "\"}\n");

        var title = Read(path)!.Title!;

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

        Assert.Null(Read(path));
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

        Assert.Equal("Edge", Read(path)!.Title);
    }

    [Fact]
    public void AMissingFileIsNotDescribed() =>
        Assert.Null(Read(Path.Combine(_temp.Path, "gone.jsonl")));
}
