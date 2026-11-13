using System.Collections.Concurrent;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using static Deguffer.Testing.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// A conversation older than the recent window is read once, not on every preview: what its head and
/// tail said is kept while its length and last-write time are as they were, and the decisions made
/// from the live listing and the live list of running sessions are made afresh each pass.
/// </summary>
public sealed class ClaudeCodeTranscriptAnswersTests : IDisposable
{
    private const int SessionProcess = 4242;
    private const string Title = "Tidy the build scripts";

    /// <summary>As long as <see cref="Title"/>, so a conversation can be written again at the same length.</summary>
    private const string SameLengthTitle = "Mend the test fixtures";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ClaudeCodeFixture _claude;
    private readonly ConcurrentQueue<string> _opened = new();
    private readonly ConcurrentDictionary<string, int> _failOnOpen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClaudeCodeTranscriptAnswers _answers;

    public ClaudeCodeTranscriptAnswersTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _claude = new ClaudeCodeFixture(_environment);
        _answers = new ClaudeCodeTranscriptAnswers(new ClaudeCodeTranscriptReader(Open));
    }

    public void Dispose() => _temp.Dispose();

    private ClaudeCodeConversationProvider Provider(FakeProcessInspector? inspector = null) =>
        new(
            _environment,
            runner: new FakeProcessRunner(),
            inspector: inspector ?? FakeProcessInspector.NothingRunning,
            transcripts: _answers);

    private static FakeProcessInspector Running() =>
        FakeProcessInspector.NothingRunning.WithProcess(SessionProcess, ProcessLiveness.Running(null));

    /// <summary>Opens a transcript as the disk would, recording it, and failing the opens a test asked to fail.</summary>
    private Stream Open(string path)
    {
        _opened.Enqueue(path);

        var name = Path.GetFileName(path);
        var count = _opened.Count(opened => Path.GetFileName(opened).Equals(name, StringComparison.OrdinalIgnoreCase));

        if (_failOnOpen.TryGetValue(name, out var failing) && failing == count)
        {
            throw new IOException("locked by the test");
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    private int Opened() => _opened.Count;

    private (string Conversation, string Folder) OldSession(
        string session, string folder = ProjectFolder, string project = ProjectPath, string title = Title) =>
        (_claude.Conversation(session, folder, project, title: title, age: Old), _claude.SessionFolder(session, folder, Old));

    private static string TitleOf(CleanupPlan plan, string conversation) =>
        Assert.Single(
            Assert.Single(plan.Steps, step => step.Subjects.Contains(conversation, StringComparer.OrdinalIgnoreCase)).Facets,
            facet => facet.Label == "Title").Value;

    /// <summary>
    /// A second pass opens no conversation that has not changed, and still decides from what is live: a
    /// session started in the project between the passes refuses its conversations, from the project the
    /// first pass read. §5.6: what is refused, the project's memory and an unrecognised file all survive.
    /// </summary>
    [Fact]
    public async Task ASecondPassReadsNoUnchangedConversationAndStillDecidesFromWhatIsRunning()
    {
        var offered = OldSession(SessionA);
        var occupied = OldSession(SessionB, OtherProjectFolder, OtherProjectPath);
        var memory = _claude.Memory();
        var unrecognised = _claude.WriteText(Path.Combine(_claude.Project(), "notes.txt"), "kept");

        var provider = Provider(Running());
        var first = await provider.PlanAsync();

        Assert.Contains(occupied.Conversation, first.TargetedPaths);
        Assert.True(Opened() > 0);

        _claude.Registered(SessionProcess, SessionC, project: OtherProjectPath);
        provider.InvalidateCaches();
        var before = Opened();

        var second = await provider.PlanAsync();

        Assert.Equal(before, Opened());
        Assert.Equal(
            new[] { offered.Conversation, offered.Folder }.Order(StringComparer.OrdinalIgnoreCase),
            second.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(Title, TitleOf(second, offered.Conversation));

        var result = await provider.ExecuteAsync(second);

        Assert.False(File.Exists(offered.Conversation));
        Assert.True(File.Exists(occupied.Conversation), "the occupied conversation was removed");
        Assert.True(Directory.Exists(occupied.Folder), "the occupied session's folder was removed");
        Assert.True(Directory.Exists(memory), "the project's memory was removed");
        Assert.True(File.Exists(unrecognised), "an unrecognised file was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Every Explore scan and every planning pass builds its rows again, so what was read is kept across
    /// providers: a declaration after a plan reads nothing, and still recognises what the plan offered.
    /// </summary>
    [Fact]
    public async Task ANewRowGivenTheSameAnswersReadsNothing()
    {
        var offered = OldSession(SessionA);

        await Provider().PlanAsync();
        var before = Opened();

        var policy = new ExploreActionPolicy([], Provider().ToolRoots, new FakeVolumeInventory());

        Assert.True(policy.MayRemove(offered.Conversation).IsAllowed);
        Assert.True(policy.MayRemove(offered.Folder).IsAllowed);
        Assert.Equal(before, Opened());
    }

    /// <summary>A conversation appended to since is read again, and shows the title it has now.</summary>
    [Fact]
    public async Task AConversationThatGrewIsReadAgain()
    {
        var (conversation, _) = OldSession(SessionA);
        var provider = Provider();

        await provider.PlanAsync();
        var written = File.GetLastWriteTimeUtc(conversation);

        File.AppendAllText(
            conversation,
            $$"""{"type":"custom-title","customTitle":"Named by the user","sessionId":"{{SessionA}}"}""" + "\n");
        File.SetLastWriteTimeUtc(conversation, written);
        provider.InvalidateCaches();
        var before = Opened();

        var plan = await provider.PlanAsync();

        Assert.True(Opened() > before);
        Assert.Equal("Named by the user", TitleOf(plan, conversation));
    }

    /// <summary>A conversation written again at the same length is read again, by its last-write time.</summary>
    [Fact]
    public async Task AConversationWrittenAgainAtTheSameLengthIsReadAgain()
    {
        var (conversation, _) = OldSession(SessionA);
        var provider = Provider();

        await provider.PlanAsync();
        var length = new FileInfo(conversation).Length;
        var written = File.GetLastWriteTimeUtc(conversation);

        File.WriteAllText(conversation, File.ReadAllText(conversation).Replace(Title, SameLengthTitle, StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(conversation, written - TimeSpan.FromMinutes(1));
        Assert.Equal(length, new FileInfo(conversation).Length);
        provider.InvalidateCaches();

        var plan = await provider.PlanAsync();

        Assert.Equal(SameLengthTitle, TitleOf(plan, conversation));
    }

    /// <summary>
    /// A conversation that goes is forgotten, so one written later at the same path, length and time is
    /// read for what it says rather than what the one before it said.
    /// </summary>
    [Fact]
    public async Task AConversationThatWentIsForgotten()
    {
        var (conversation, _) = OldSession(SessionA);
        var provider = Provider();

        await provider.PlanAsync();
        var text = File.ReadAllText(conversation);
        var written = File.GetLastWriteTimeUtc(conversation);

        File.Delete(conversation);
        provider.InvalidateCaches();
        await provider.PlanAsync();

        File.WriteAllText(conversation, text.Replace(Title, SameLengthTitle, StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(conversation, written);
        provider.InvalidateCaches();

        var plan = await provider.PlanAsync();

        Assert.Equal(SameLengthTitle, TitleOf(plan, conversation));
    }

    /// <summary>
    /// A read that failed is not kept. The conversation is held back while its title cannot be read, and
    /// §5.6 asserts it, and the next pass reads it again and offers it.
    /// </summary>
    [Fact]
    public async Task ATitleThatCouldNotBeReadIsReadAgainNextPass()
    {
        var (conversation, folder) = OldSession(SessionA);
        _failOnOpen[Path.GetFileName(conversation)] = 2;
        var provider = Provider();

        var first = await provider.PlanAsync();

        Assert.Empty(first.TargetedPaths);
        Assert.Contains(first.ProtectedPaths, kept => kept.Path.Equals(conversation, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(first.ProtectedPaths, kept => kept.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));

        provider.InvalidateCaches();

        var second = await provider.PlanAsync();

        Assert.Equal(Title, TitleOf(second, conversation));
        Assert.Equal(3, Opened());
    }

    /// <summary>A conversation that could not be placed is read again next pass, and placed once it can be.</summary>
    [Fact]
    public async Task APlacementThatCouldNotBeReadIsReadAgainNextPass()
    {
        var (conversation, _) = OldSession(SessionA);
        _failOnOpen[Path.GetFileName(conversation)] = 1;
        var provider = Provider();

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        provider.InvalidateCaches();

        Assert.Equal(Title, TitleOf(await provider.PlanAsync(), conversation));
    }
}
