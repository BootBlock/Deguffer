using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;
using static Deguffer.Core.Tests.Fakes.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// Claude Code's conversations, offered at Tier 3 one session at a time: the conversation and the
/// session's folder, each a step sharing the session's identity.
///
/// <para>Everything runs against an invented folder through <see cref="FakeUserEnvironment"/> and
/// <see cref="FakeProcessInspector"/>. Every conversation is invented, and no test reads a real
/// one.</para>
/// </summary>
public sealed class ClaudeCodeConversationProviderTests : IDisposable
{
    private const int SessionProcess = 4242;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ClaudeCodeFixture _claude;

    public ClaudeCodeConversationProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _claude = new ClaudeCodeFixture(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private ClaudeCodeConversationProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, runner: new FakeProcessRunner(), inspector: inspector ?? FakeProcessInspector.NothingRunning);

    private static FakeProcessInspector Running() =>
        FakeProcessInspector.NothingRunning.WithProcess(SessionProcess, ProcessLiveness.Running(null));

    /// <summary>An ended session: its conversation and its folder, both last written a month ago.</summary>
    private (string Conversation, string Folder) OldSession(string session, string folder = ProjectFolder, string project = ProjectPath) =>
        (_claude.Conversation(session, folder, project, age: Old), _claude.SessionFolder(session, folder, Old));

    private static void AssertKept(CleanupPlan plan, string path)
    {
        Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    private static string Facet(CleanupStep step, string label) =>
        Assert.Single(step.Facets, facet => facet.Label == label).Value;

    [Fact]
    public async Task ReportsNotPresentWhereClaudeCodeHasKeptNoConversations()
    {
        _claude.HookEnvironment(SessionA, age: Old);

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// One session is two steps, the conversation and its folder, at Tier 3, listed as items and never
    /// chosen for the user. Both carry the session's identity, so the keep list treats them as one.
    /// </summary>
    [Fact]
    public async Task OffersEachEndedSessionsConversationAndFolderAtTier3AndNeverChoosesThem()
    {
        var (conversation, folder) = OldSession(SessionA);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { conversation, folder }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.UserData, plan.Tier);
        Assert.Equal(StepGrain.Items, provider.Grain);
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
        Assert.IsType<DeleteFileStep>(Assert.Single(plan.Steps, step => ((DeleteStep)step).Path == conversation));
        Assert.All(plan.Steps, step =>
        {
            Assert.Equal(new ItemIdentity(SessionA, "Tidy the build scripts"), step.Identity);
            Assert.Equal(ProjectPath, step.Group);
            Assert.Equal("Tidy the build scripts", Facet(step, "Title"));
            Assert.NotNull(((DeleteStep)step).UseCheck);
        });

        var result = await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(conversation), "the conversation was not removed");
        Assert.False(Directory.Exists(folder), "the session's folder was not removed");
        Assert.True(Directory.Exists(_claude.Project()), "the project folder was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Every item says when Claude Code would have deleted it anyway: its last use plus the retention period
    /// the user's settings set, 30 days where they set none.
    /// </summary>
    [Theory]
    [InlineData(null, 30)]
    [InlineData("{}", 30)]
    [InlineData("{\"cleanupPeriodDays\": 45}", 45)]
    public async Task EachItemSaysWhenClaudeCodeWouldHaveDeletedItItself(string? settings, int days)
    {
        var (conversation, _) = OldSession(SessionA);

        if (settings is not null)
        {
            _claude.Settings(settings);
        }

        var plan = await CreateProvider().PlanAsync();
        var expires = (File.GetLastWriteTimeUtc(conversation) + TimeSpan.FromDays(days)).ToLocalTime().ToString("yyyy-MM-dd");

        Assert.All(plan.Steps, step => Assert.Equal(expires, Facet(step, "Expires")));
        Assert.Contains(plan.Notes, note => note.Message.Contains($"{days} days after it was last used", StringComparison.Ordinal));
        Assert.All(plan.Steps.OfType<DeleteFileStep>(), step => Assert.Contains("Claude Code deletes it itself", step.What, StringComparison.Ordinal));
    }

    /// <summary>A period Claude Code itself would reject, or settings nobody can read, give no date rather than a wrong one.</summary>
    [Theory]
    [InlineData("{\"cleanupPeriodDays\": 0}")]
    [InlineData("{\"cleanupPeriodDays\": \"thirty\"}")]
    [InlineData("{ not json")]
    public async Task ARetentionPeriodThatCannotBeReadGivesNoDate(string settings)
    {
        OldSession(SessionA);
        _claude.Settings(settings);

        var plan = await CreateProvider().PlanAsync();

        Assert.NotEmpty(plan.Steps);
        Assert.All(plan.Steps, step => Assert.Equal("Unknown", Facet(step, "Expires")));
        Assert.Contains(plan.Notes, note => note.Message.Contains("could not read how long", StringComparison.Ordinal));
        Assert.All(plan.Steps.OfType<DeleteFileStep>(), step =>
        {
            Assert.DoesNotContain("the date under Expires", step.What, StringComparison.Ordinal);
            Assert.Contains("as long as your settings say", step.What, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A period long enough to mean "keep everything" is one Claude Code accepts, so it is read, and no date
    /// past the end of the calendar is worked out from it.
    /// </summary>
    [Theory]
    [InlineData("{\"cleanupPeriodDays\": 99999999}")]
    [InlineData("{\"cleanupPeriodDays\": 9999999999999}")]
    public async Task ARetentionPeriodThatKeepsEverythingIsShownAsSuch(string settings)
    {
        OldSession(SessionA);
        _claude.Settings(settings);

        var plan = await CreateProvider().PlanAsync();

        Assert.NotEmpty(plan.Steps);
        Assert.All(plan.Steps, step => Assert.Equal("Not within 100 years", Facet(step, "Expires")));
    }

    /// <summary>
    /// The title Claude Code shows now: the user's own over a generated one, and a later generated one over
    /// an earlier one, even with a long conversation between them.
    /// </summary>
    [Theory]
    [InlineData("First title", null, null, "First title")]
    [InlineData("First title", "Later title", null, "Later title")]
    [InlineData("First title", "Later title", "My own name", "My own name")]
    public async Task TheTitleIsTheOneClaudeCodeShowsNow(string title, string? late, string? custom, string expected)
    {
        _claude.Conversation(SessionA, title: title, lateTitle: late, customTitle: custom, age: Old, padding: 2 * 1024 * 1024);

        var plan = await CreateProvider().PlanAsync();
        var step = Assert.Single(plan.Steps);

        Assert.Equal(expected, Facet(step, "Title"));
        Assert.Equal(expected, step.Identity!.Name);
    }

    /// <summary>
    /// Fifteen of 1,405 measured conversations had no title. Such a session is named by its project and
    /// its date, and never by the last prompt, which is the user's own words and is on the last line.
    /// </summary>
    [Fact]
    public async Task AnUntitledConversationIsNamedByItsProjectAndDateAndNeverByItsLastPrompt()
    {
        var started = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        _claude.Conversation(SessionA, title: null, started: started, age: Old);

        var plan = await CreateProvider().PlanAsync();
        var step = Assert.Single(plan.Steps);
        var day = started.UtcDateTime.ToLocalTime().ToString("yyyy-MM-dd");

        Assert.Equal("Untitled", Facet(step, "Title"));
        Assert.Equal(day, Facet(step, "Started"));
        Assert.Equal($"Untitled conversation in example, {day}", step.Identity!.Name);
        Assert.DoesNotContain(plan.Steps.SelectMany(s => s.Facets), facet => facet.Value.Contains(LastPrompt, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, note => note.Message.Contains(LastPrompt, StringComparison.Ordinal));
    }

    /// <summary>
    /// A conversation that does not say which project it belongs to cannot be described well enough to
    /// choose, which a format change would cause. It is not offered, it and its folder are asserted, and the
    /// row says how many it left.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConversationThatCannotBePlacedInAProjectIsNeverOffered(bool garbled)
    {
        var offered = OldSession(SessionB);
        var folder = _claude.SessionFolder(SessionA, age: Old);
        var conversation = garbled
            ? _claude.WriteText(Path.Combine(_claude.Project(), SessionA + ".jsonl"), "{ not json\n<<binary>>\n")
            : _claude.Conversation(SessionA, project: null);

        TempDirectory.Age(conversation, Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { offered.Conversation, offered.Folder }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        AssertKept(plan, conversation);
        AssertKept(plan, folder);
        Assert.Contains(plan.Notes, note => note.Message.Contains("Left 1 conversation alone that Deguffer could not read", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(conversation), "a conversation nobody could describe was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A session the list names is refused outright, and released only once its process has been asked
    /// about and has ended. The entry records another project, as a session resumed from another folder
    /// does, so the session is refused by its id and not by its project.
    /// </summary>
    [Theory]
    [InlineData(ProcessState.NotRunning, true)]
    [InlineData(ProcessState.Running, false)]
    [InlineData(ProcessState.Undetermined, false)]
    public async Task ASessionListedAsRunningIsOfferedOnlyOnceItsProcessHasEnded(ProcessState state, bool offered)
    {
        var (conversation, folder) = OldSession(SessionA);
        _claude.Registered(SessionProcess, SessionA, project: OtherProjectPath);

        var inspector = state switch
        {
            ProcessState.Running => Running(),
            ProcessState.Undetermined => FakeProcessInspector.NothingRunning.WithProcess(SessionProcess, ProcessLiveness.Undetermined),
            _ => FakeProcessInspector.NothingRunning,
        };

        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        if (offered)
        {
            Assert.Contains(conversation, plan.TargetedPaths);
            return;
        }

        AssertKept(plan, conversation);
        AssertKept(plan, folder);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(conversation), "a running session's conversation was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// "Nothing is running" and "we could not tell" lead to opposite decisions: an unreadable list refuses
    /// every conversation. An entry whose session id holds an unpaired surrogate parses, and cannot be read.
    /// </summary>
    [Theory]
    [InlineData("{ not an entry")]
    [InlineData("{\"pid\":4242,\"sessionId\":\"\\ud800\"}")]
    public async Task AListThatCannotBeReadRefusesEveryConversation(string entry)
    {
        var sessions = new[] { OldSession(SessionA), OldSession(SessionB, OtherProjectFolder, OtherProjectPath) };
        _claude.WriteText(Path.Combine(_claude.Sessions, $"{SessionProcess}.json"), entry);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);

        foreach (var (conversation, folder) in sessions)
        {
            AssertKept(plan, conversation);
            AssertKept(plan, folder);
        }

        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning
            && note.Message.Contains("list of running sessions", StringComparison.Ordinal));
    }

    /// <summary>
    /// A running process can resume any conversation of its project, and the list goes on naming the session
    /// it started with. So a session running in a project refuses every conversation in it, and none in
    /// another project.
    /// </summary>
    [Fact]
    public async Task ASessionRunningInAProjectRefusesEveryConversationInIt()
    {
        var sameProject = OldSession(SessionA);
        var otherProject = OldSession(SessionB, OtherProjectFolder, OtherProjectPath);
        _claude.Registered(SessionProcess, SessionC, project: ProjectPath + "\\");

        var plan = await CreateProvider(Running()).PlanAsync();

        AssertKept(plan, sameProject.Conversation);
        AssertKept(plan, sameProject.Folder);
        Assert.Contains(otherProject.Conversation, plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == sameProject.Conversation && p.Reason.Contains("could resume it", StringComparison.Ordinal));
    }

    /// <summary>A running process whose folder the list does not record could be in any project.</summary>
    [Fact]
    public async Task ARunningSessionWithNoRecordedProjectRefusesEveryConversation()
    {
        var sessions = new[] { OldSession(SessionA), OldSession(SessionB, OtherProjectFolder, OtherProjectPath) };
        _claude.Registered(SessionProcess, SessionC, project: null);

        var plan = await CreateProvider(Running()).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.All(sessions, session => AssertKept(plan, session.Conversation));
    }

    /// <summary>
    /// A live conversation is appended to continuously, and the guard on recently changed files is off by
    /// default. The provider's own floor holds a session back if either part was written this week.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASessionUsedThisWeekIsHeldBack(bool conversationRecent)
    {
        var conversation = _claude.Conversation(SessionA, age: conversationRecent ? TimeSpan.FromHours(1) : Old);
        var folder = _claude.SessionFolder(SessionA, age: conversationRecent ? Old : null);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == conversation && p.Withheld == Withholding.TooRecent);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder && p.Withheld == Withholding.TooRecent);
        Assert.True(plan.HasRecentContentHeldBack);
    }

    /// <summary>
    /// The floor travels on the plan, not only into the preview. A conversation appended to between the
    /// preview and the clean, by a version of Claude Code the list does not show, is spared by the removal.
    /// </summary>
    [Fact]
    public async Task AConversationWrittenAfterThePreviewIsSparedByTheClean()
    {
        var (conversation, folder) = OldSession(SessionA);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(conversation, plan.TargetedPaths);

        File.AppendAllText(conversation, "{\"type\":\"assistant\"}\n");
        File.SetCreationTimeUtc(conversation, DateTime.UtcNow);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(conversation), "a conversation written after the preview was removed");
        Assert.True(Directory.Exists(folder), "the folder of a session in use was removed without its conversation");
        Assert.True(File.Exists(Path.Combine(folder, "subagents", "agent-0123456789abcdef.jsonl")), "part of a session in use was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Three session folders were measured under a different project folder from their own conversation. The
    /// folder goes with its conversation wherever it is, and its project folder is declared for Explore.
    /// </summary>
    [Fact]
    public async Task ASessionsFolderUnderAnotherProjectFolderGoesWithItsConversation()
    {
        var conversation = _claude.Conversation(SessionA, age: Old);
        var folder = _claude.SessionFolder(SessionA, OtherProjectFolder, Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { conversation, folder }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        Assert.True(policy.MayRemove(folder).IsAllowed, "Explore refused a session folder the Storage page would clean");
    }

    /// <summary>
    /// A session folder whose conversation has gone is the leftovers row's to decide about, so this row
    /// neither offers nor asserts it: asserting it would read that row's removal as a failure of this one.
    /// </summary>
    [Fact]
    public async Task ASessionFolderWithoutAConversationIsNeitherOfferedNorAsserted()
    {
        OldSession(SessionA);
        var orphan = _claude.SessionFolder(SessionB, age: Old);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(orphan, plan.TargetedPaths);
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(orphan, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Two conversations under one session's name leave nobody able to say whose folder is whose, so neither is offered.</summary>
    [Fact]
    public async Task ASessionNameUsedByTwoConversationsIsRefused()
    {
        var first = _claude.Conversation(SessionA, age: Old);
        var second = _claude.Conversation(SessionA, OtherProjectFolder, OtherProjectPath, age: Old);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        AssertKept(plan, first);
        AssertKept(plan, second);
    }

    /// <summary>
    /// §5.2 inside a project folder, including names Claude Code writes that were not offered on the machine
    /// this was measured on: a set-aside conversation, the project's memory, and folders shaped almost like a
    /// session's.
    /// </summary>
    [Theory]
    [InlineData("memory", true)]
    [InlineData("not-a-session", true)]
    [InlineData(SessionC + ".bak", true)]
    [InlineData(SessionC + ".orphaned-1700000000000-abc.jsonl", false)]
    [InlineData(SessionC + ".jsonl.superseded-1700000000000", false)]
    [InlineData("notes.jsonl", false)]
    public async Task AnythingInAProjectFolderThatIsNotAConversationIsTier4AndSurvives(string name, bool isFolder)
    {
        OldSession(SessionA);

        var path = Path.Combine(_claude.Project(), name);

        if (isFolder)
        {
            var inside = _claude.CreateFile(Path.Combine(path, "MEMORY.md"), 64);
            TempDirectory.Age(inside, Old);
            AgeFolder(path, Old);
        }
        else
        {
            _claude.WriteText(path, "{\"type\":\"user\",\"cwd\":\"C:\\\\Users\\\\testuser\\\\src\\\\example\"}\n");
            TempDirectory.Age(path, Old);
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.Steps);
        AssertKept(plan, path);

        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(path).IsAllowed, $"Explore would remove {name}");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(path) || Directory.Exists(path), $"{name} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.6's negative over what this row must prove it left: Claude Code's folder, the projects folder, the
    /// project folder, its memory, the sign-in, and a running session's conversation and folder.
    /// </summary>
    [Fact]
    public async Task EverythingThisRowMustLeaveIsAssertedAndDoesSurvive()
    {
        OldSession(SessionA);

        var running = OldSession(SessionB, OtherProjectFolder, OtherProjectPath);
        _claude.Registered(SessionProcess, SessionB, project: OtherProjectPath);

        var credentials = _claude.WriteText(Path.Combine(_claude.Home, ".credentials.json"), "{}");
        string[] mustSurvive =
            [_claude.Home, _claude.Projects, _claude.Project(), _claude.Memory(), credentials, running.Conversation, running.Folder];

        var provider = CreateProvider(Running());
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.Steps);

        foreach (var path in mustSurvive)
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        foreach (var path in mustSurvive)
        {
            Assert.True(File.Exists(path) || Directory.Exists(path), $"{path} did not survive the clean.");
        }

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The scope of that negative, run beside the other two rows over Claude Code's folder, sharing one list
    /// and one walk. The leftovers row takes a session folder whose conversation is gone and this row takes a
    /// whole session, and all three verify.
    /// </summary>
    [Fact]
    public async Task ARunWithTheOtherClaudeCodeRowsVerifiesAll()
    {
        var (conversation, folder) = OldSession(SessionA);
        var orphan = _claude.SpilledOutput(SessionB, age: Old);
        var snapshots = _claude.RewindSnapshots(SessionA, folderAge: Old, snapshotAge: Old);

        var sessions = new ClaudeCodeSessionRegistry(_environment, FakeProcessInspector.NothingRunning);
        var projects = new ClaudeCodeProjectsDiscovery(_environment);
        var runner = new FakeProcessRunner();
        var inspector = FakeProcessInspector.NothingRunning;

        ICleanupProvider[] providers =
        [
            new ClaudeCodeDerivedStateProvider(_environment, sessions, runner, inspector, projects: projects),
            new ClaudeCodeFileHistoryProvider(_environment, sessions, runner, inspector),
            new ClaudeCodeConversationProvider(_environment, sessions, runner, inspector, projects: projects),
        ];

        var plans = new List<CleanupPlan>();

        foreach (var provider in providers)
        {
            plans.Add(await provider.PlanAsync());
        }

        Assert.Contains(orphan, plans[0].TargetedPaths);
        Assert.Equal([snapshots], plans[1].TargetedPaths);
        Assert.Contains(conversation, plans[2].TargetedPaths);

        var reach = RunReach.Of(plans);

        foreach (var (provider, plan) in providers.Zip(plans))
        {
            var result = await provider.ExecuteAsync(plan, reach);

            Assert.True(result.Verification!.Passed, $"{provider.Name}: {result.Verification.Summary}");
        }

        Assert.False(File.Exists(conversation));
        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(orphan));
    }

    /// <summary>Keeping a session keeps both of its parts, and both are asserted to survive.</summary>
    [Fact]
    public async Task KeepingASessionKeepsItsConversationAndItsFolder()
    {
        var kept = OldSession(SessionA);
        var other = OldSession(SessionB);

        var provider = CreateProvider();
        var plan = (await provider.PlanAsync()).WithKeepList(new HashSet<string>([SessionA], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(
            new[] { other.Conversation, other.Folder }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == kept.Conversation && p.Withheld == Withholding.OnKeepList);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == kept.Folder && p.Withheld == Withholding.OnKeepList);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(kept.Conversation), "a kept conversation was removed");
        Assert.True(Directory.Exists(kept.Folder), "a kept session's folder was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The preview can sit on screen while the user resumes a conversation, or starts Claude Code in its
    /// project. Either makes the preview's answer wrong in the direction that deletes, so the clean asks
    /// again and leaves the whole session.
    /// </summary>
    [Theory]
    [InlineData(SessionA, ProjectPath)]
    [InlineData(SessionC, ProjectPath)]
    public async Task ASessionResumedOrAProjectOpenedAfterThePreviewKeepsTheConversation(string started, string project)
    {
        var (conversation, folder) = OldSession(SessionA);
        var ended = OldSession(SessionB, OtherProjectFolder, OtherProjectPath);

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.Contains(conversation, plan.TargetedPaths);

        _claude.Registered(SessionProcess, started, project: project);
        inspector.WithProcess(SessionProcess, ProcessLiveness.Running(null));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(conversation), "a conversation Claude Code could resume was removed");
        Assert.True(Directory.Exists(folder), "the folder of a session Claude Code could resume was removed");
        Assert.False(File.Exists(ended.Conversation), "a conversation in another project was kept");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A subagent appending to its conversation deep inside the session folder moves no timestamp the
    /// classification reads. The measurement sees it, and the whole session is held back rather than
    /// removed around the recent file.
    /// </summary>
    [Fact]
    public async Task ASessionWithRecentContentDeepInItsFolderIsHeldBackWhole()
    {
        var (conversation, folder) = OldSession(SessionA);
        var subagent = Path.Combine(folder, "subagents", "agent-0123456789abcdef.jsonl");
        var subagents = Path.GetDirectoryName(subagent)!;

        File.AppendAllText(subagent, "{}\n");
        File.SetCreationTimeUtc(subagent, DateTime.UtcNow);
        AgeFolder(subagents, Old);
        AgeFolder(folder, Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == conversation && p.Withheld == Withholding.TooRecent);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder && p.Withheld == Withholding.TooRecent);
    }

    /// <summary>
    /// Claude Code rewrites a running session's entry when it moves into a subfolder or a worktree beside
    /// the project. The project stays occupied: by the folder the entry names, when that is inside it, and
    /// by the folder the session's own conversation says it started in.
    /// </summary>
    [Theory]
    [InlineData(ProjectPath + @"\src\tools", false)]
    [InlineData(@"C:\Users\testuser\src\example-worktree", true)]
    public async Task ASessionThatMovedOnStillOccupiesItsProject(string workingIn, bool startedInProject)
    {
        var (conversation, folder) = OldSession(SessionA);
        var other = OldSession(SessionB, OtherProjectFolder, OtherProjectPath);

        if (startedInProject)
        {
            _claude.Conversation(SessionC, age: TimeSpan.FromHours(1));
        }

        _claude.Registered(SessionProcess, SessionC, project: workingIn);

        var plan = await CreateProvider(Running()).PlanAsync();

        AssertKept(plan, conversation);
        AssertKept(plan, folder);
        Assert.Contains(other.Conversation, plan.TargetedPaths);
    }

    /// <summary>A folder that only begins with the project's name is a different project.</summary>
    [Fact]
    public async Task AFolderThatOnlySharesItsNamesStartIsAnotherProject()
    {
        var (conversation, _) = OldSession(SessionA);
        _claude.Registered(SessionProcess, SessionC, project: ProjectPath + "-other");

        var plan = await CreateProvider(Running()).PlanAsync();

        Assert.Contains(conversation, plan.TargetedPaths);
    }

    /// <summary>
    /// Which folders are a session's, and whether its name is its own, are answered across every project
    /// folder. One that cannot be listed may hold the answer, so nothing is offered, and the row says so.
    /// </summary>
    [Fact]
    public async Task AProjectFolderThatCannotBeListedRefusesEveryConversation()
    {
        var (conversation, folder) = OldSession(SessionA);
        var unlisted = _claude.Project(OtherProjectFolder);
        _claude.CreateFile(Path.Combine(unlisted, "notes.md"), 64);

        using var denied = new DeniedDirectory(unlisted);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        AssertKept(plan, conversation);
        AssertKept(plan, folder);
        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning
            && note.Message.Contains("could not list every Claude Code project folder", StringComparison.Ordinal));
    }

    /// <summary>A conversation that is a link is named and refused, and so is the session's folder.</summary>
    [Fact]
    public async Task AConversationThatIsALinkRefusesTheWholeSession()
    {
        var outside = _claude.Conversation(SessionB, OtherProjectFolder, OtherProjectPath, age: Old);
        var folder = _claude.SessionFolder(SessionA, age: Old);
        var link = Path.Combine(_claude.Project(), SessionA + ".jsonl");

        SymbolicLink.ToFile(link, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(link, plan.TargetedPaths);
        Assert.DoesNotContain(folder, plan.TargetedPaths);
        AssertKept(plan, folder);

        // Kept by the rule about links, and not by the floor on recent content, which a new link also meets.
        Assert.Contains(plan.ProtectedPaths, p => p.Path == link && p.Reason == CacheLevelWalk.LinkReason);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder && p.Reason.Contains("is a link", StringComparison.Ordinal));
    }

    /// <summary>A session folder that is a link is named and refused, and so is the rest of its session.</summary>
    [Fact]
    public async Task ASessionFolderThatIsALinkRefusesTheWholeSession()
    {
        var conversation = _claude.Conversation(SessionA, age: Old);
        var outside = Path.Combine(_temp.Path, "outside-session");
        TempDirectory.Age(_claude.CreateFile(Path.Combine(outside, "note.md"), 64), Old);

        var link = Path.Combine(_claude.Project(), SessionA);
        SymbolicLink.ToDirectory(link, outside);
        AgeFolder(link, Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        AssertKept(plan, conversation);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(conversation));
        Assert.True(Directory.Exists(outside));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }
}
