using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using static Deguffer.Testing.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// What Explore's policy costs the Claude Code conversations row, and that it decides the same things
/// for that cost: the first lines of a conversation are enough to say which are offered, a title is
/// read only for a plan, and a scope that cannot reach the conversations reads none of them.
/// </summary>
public sealed class ClaudeCodeConversationDeclarationTests : IDisposable
{
    private const int SessionProcess = 4242;
    private const int FirstRead = 4 * 1024;
    private const int Padded = 200 * 1024;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ClaudeCodeFixture _claude;
    private readonly FakeVolumeInventory _volumes = new();
    private readonly RecordedTranscriptReads _reads = new();

    public ClaudeCodeConversationDeclarationTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _claude = new ClaudeCodeFixture(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private ClaudeCodeConversationProvider Provider(ProtectionScope? scope = null, FakeProcessInspector? inspector = null) =>
        new(
            _environment,
            runner: new FakeProcessRunner(),
            inspector: inspector ?? FakeProcessInspector.NothingRunning,
            scope: scope,
            transcripts: _reads.Reader);

    private ExploreActionPolicy Policy(IEnumerable<ToolRoot> roots, IEnumerable<ToolRoot>? probed = null) =>
        new([], roots, _volumes, probedRoots: probed);

    private (string Conversation, string Folder) OldSession(string session, string folder = ProjectFolder, string project = ProjectPath) =>
        (_claude.Conversation(session, folder, project, age: Old, padding: Padded), _claude.SessionFolder(session, folder, Old));

    /// <summary>
    /// The declaration reads no further than each conversation's first lines, and refuses and offers what
    /// the plan does: §5.6's negative is everything here that stays refused.
    /// </summary>
    [Fact]
    public async Task DeclaringReadsOnlyFirstLinesAndRecognisesExactlyWhatThePlanOffers()
    {
        var offered = OldSession(SessionA);
        var unplaced = _claude.Conversation(SessionB, OtherProjectFolder, project: null, age: Old);
        var recent = _claude.Conversation(SessionC);
        var memory = Directory.CreateDirectory(Path.Combine(_claude.Project(), "memory")).FullName;
        var unrecognised = _claude.WriteText(Path.Combine(_claude.Project(), "notes.txt"), "kept");

        var policy = Policy(Provider().ToolRoots);

        Assert.InRange(_reads.Furthest, 1, FirstRead);

        var plan = await Provider().PlanAsync();

        Assert.Equal(
            new[] { offered.Conversation, offered.Folder }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan.TargetedPaths, path => Assert.True(policy.MayRemove(path).IsAllowed, $"Explore refused {path}"));

        foreach (var refused in new[] { unplaced, recent, memory, unrecognised, _claude.Project(), _claude.Projects, _claude.Home })
        {
            Assert.False(policy.MayRemove(refused).IsAllowed, $"Explore would remove {refused}");
        }
    }

    /// <summary>Asking where a running session started is a question for its first lines too.</summary>
    [Fact]
    public void ARunningSessionIsPlacedFromItsFirstLines()
    {
        var running = OldSession(SessionA);
        var ended = OldSession(SessionB, OtherProjectFolder, OtherProjectPath);
        _claude.Registered(SessionProcess, SessionA, project: ProjectPath);
        var inspector = FakeProcessInspector.NothingRunning.WithProcess(SessionProcess, ProcessLiveness.Running(null));

        var policy = Policy(Provider(inspector: inspector).ToolRoots);

        Assert.InRange(_reads.Furthest, 1, FirstRead);
        Assert.Contains(_reads.Opened, opened => opened.EndsWith(SessionA + ".jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.False(policy.MayRemove(running.Conversation).IsAllowed);
        Assert.True(policy.MayRemove(ended.Conversation).IsAllowed);
    }

    /// <summary>A plan reads a title only for what it offers, and shows the title it read.</summary>
    [Fact]
    public async Task APlanReadsATitleOnlyForWhatItOffers()
    {
        var offered = OldSession(SessionA);
        var unplaced = _claude.Conversation(SessionB, OtherProjectFolder, project: null, age: Old);

        var plan = await Provider().PlanAsync();

        Assert.Equal(2, _reads.Opened.Count(opened => opened.EndsWith(Path.GetFileName(offered.Conversation), StringComparison.OrdinalIgnoreCase)));
        Assert.Single(_reads.Opened, opened => opened.EndsWith(Path.GetFileName(unplaced), StringComparison.OrdinalIgnoreCase));
        Assert.All(plan.Steps, step => Assert.Equal("Tidy the build scripts", Assert.Single(step.Facets, facet => facet.Label == "Title").Value));
    }

    /// <summary>
    /// A title that cannot be read leaves the conversation out of the plan, as it always did, with its
    /// folder, asserted to survive, and counted with those that cannot be placed.
    /// </summary>
    [Fact]
    public async Task AConversationWhoseTitleCannotBeReadIsNotOffered()
    {
        var offered = OldSession(SessionA);
        var conversation = _claude.WriteText(
            Path.Combine(_claude.Project(OtherProjectFolder), SessionB + ".jsonl"),
            "{\"type\":\"user\",\"cwd\":\"C:\\\\p\",\"timestamp\":\"2026-07-01T12:00:00Z\"}\n"
            + "{\"type\":\"ai-title\",\"aiTitle\":\"\\ud800\"}\n");
        TempDirectory.Age(conversation, Old);
        var folder = _claude.SessionFolder(SessionB, OtherProjectFolder, Old);

        var provider = Provider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { offered.Conversation, offered.Folder }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, kept => kept.Path.Equals(conversation, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, kept => kept.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, note => note.Message.Contains("Left 1 conversation alone that Deguffer could not read", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(conversation), "the conversation was removed");
        Assert.True(Directory.Exists(folder), "the session's folder was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A scope that cannot reach the conversations reads none of them, and what it never looked at is
    /// refused rather than left open. A plan is not scoped.
    /// </summary>
    [Fact]
    public async Task AScopeOutsideTheConversationsReadsNoneAndRefusesThemWhole()
    {
        var (conversation, folder) = OldSession(SessionA);
        var provider = Provider(ProtectionScope.Within([Path.Combine(_temp.Path, "elsewhere")], _volumes));

        var probed = await provider.DiscoverToolRootsAsync();
        var roots = provider.ToolRoots;

        Assert.Empty(_reads.Opened);
        Assert.Equal(_claude.Projects, Assert.Single(probed).Path, StringComparer.OrdinalIgnoreCase);

        var policy = Policy(roots, probed);

        foreach (var refused in new[] { conversation, folder, _claude.Project(), _claude.Projects, _claude.Home })
        {
            Assert.False(policy.MayRemove(refused).IsAllowed, $"Explore would remove {refused}");
        }

        Assert.Equal(
            new[] { conversation, folder }.Order(StringComparer.OrdinalIgnoreCase),
            (await provider.PlanAsync()).TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Every scope that may ask about a conversation gets the row's whole answer.</summary>
    [Theory]
    [InlineData("everything")]
    [InlineData("the profile")]
    [InlineData("the home")]
    [InlineData("a project")]
    [InlineData("a scan that ended without a tree")]
    public async Task AScopeThatCanReachTheConversationsReadsThem(string reach)
    {
        var (conversation, _) = OldSession(SessionA);
        var scope = reach switch
        {
            "everything" => ProtectionScope.Everything,
            "the profile" => ProtectionScope.Within([_temp.Path], _volumes),
            "the home" => ProtectionScope.Within([_claude.Home], _volumes),
            "a project" => ProtectionScope.Within([_claude.Project()], _volumes),
            _ => ProtectionScope.Scanned(Path.Combine(_temp.Path, "elsewhere"), Task.FromResult<ExploreScan?>(null), _volumes),
        };
        var provider = Provider(scope);

        var probed = await provider.DiscoverToolRootsAsync();

        Assert.Empty(probed);
        Assert.True(Policy(provider.ToolRoots, probed).MayRemove(conversation).IsAllowed);
        Assert.NotEmpty(_reads.Opened);
    }

    /// <summary>The scope reaches the row through the one place the app builds it.</summary>
    [Fact]
    public async Task ThePlannersRowIsGivenItsScope()
    {
        var scope = ProtectionScope.Within([Path.Combine(_temp.Path, "elsewhere")], _volumes);
        var row = CleanupPlanner.CreateDefault(liveTrees: FakeLiveTreeInspector.NothingLive, environment: _environment, scope: scope)
            .Providers.Single(provider => provider.Id == "claude-code-conversations");
        OldSession(SessionA);

        Assert.Equal(_claude.Projects, Assert.Single(await row.DiscoverToolRootsAsync()).Path, StringComparer.OrdinalIgnoreCase);
    }
}
