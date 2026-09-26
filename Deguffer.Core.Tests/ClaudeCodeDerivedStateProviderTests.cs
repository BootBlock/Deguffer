using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using static Deguffer.Testing.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// What Claude Code's sessions leave behind, asserted mostly by what must never be touched.
///
/// <para>Claude Code's folder holds every conversation the user has had with it, each project's memory,
/// their settings and their sign-in, beside the derived state this provider offers. So these are §5.2
/// and §5.6 tests first: every folder at the top level is left alone, anything not recognised is Tier 4,
/// and what must survive is asserted to have survived. Whether a process or a session has ended is the
/// subject of <see cref="ClaudeCodeLivenessTests"/>.</para>
///
/// <para>Everything runs against an invented folder through <see cref="FakeUserEnvironment"/> and
/// <see cref="FakeProcessInspector"/>. No test needs Claude Code installed, and none reads a real
/// one.</para>
/// </summary>
public sealed class ClaudeCodeDerivedStateProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ClaudeCodeFixture _claude;

    public ClaudeCodeDerivedStateProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _claude = new ClaudeCodeFixture(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private ClaudeCodeDerivedStateProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, runner: new FakeProcessRunner(), inspector: inspector ?? FakeProcessInspector.NothingRunning);

    /// <summary>One old leftover of every kind, for a test that needs the plan to have something in it.</summary>
    private IReadOnlyList<string> CreateOneOfEachLeftover() =>
    [
        _claude.SpilledOutput(SessionA, age: Old),
        _claude.HookEnvironment(SessionA, age: Old),
        _claude.EditorLock(51234, processId: 4101),
        _claude.MessagingKey(4102),
        _claude.ShellSnapshot(DateTime.UtcNow - Old),
        _claude.FailedEvents(SessionA, age: Old),
    ];

    /// <summary>
    /// One of the folders a clean works in, which Windows will not describe while the others are
    /// cleaned. §5.6 asserts it, recorded as a refusal, so the check after the run either sees it or
    /// says it could not. Left out, it was a folder beside the run's own deletions that nothing could
    /// fail over.
    /// </summary>
    [Fact]
    public async Task AFolderWindowsWillNotDescribeBesideTheOthersIsStillASurvivor()
    {
        CreateOneOfEachLeftover();
        var snapshots = Path.Combine(_claude.Home, ClaudeCodeHome.ShellSnapshots);

        using var denied = DeniedDirectory.WithUnreadableAttributes(snapshots);

        var plan = await CreateProvider().PlanAsync();

        Assert.NotEmpty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(snapshots, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Refused);
    }

    /// <summary>
    /// A Claude Code folder Windows will not describe is declared all the same, with the two
    /// neighbours the plan names. The declaration dropped it, so Explore offered every leftover in a
    /// folder the Storage page said nothing was ruled out in.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesAllOfAFolderWindowsWillNotDescribe()
    {
        var leftovers = CreateOneOfEachLeftover();

        using var denied = DeniedDirectory.WithUnreadableAttributes(_claude.Home);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);

        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        foreach (var refused in leftovers.Concat(
        [
            _claude.Home,
            Path.Combine(_environment.UserProfile, ".claude.json"),
            Path.Combine(_environment.UserProfile, ".claude-swap-backup"),
        ]))
        {
            Assert.False(policy.MayRemove(refused).IsAllowed, $"Explore would remove {refused}");
        }
    }

    [Fact]
    public async Task ReportsNotPresentWhereClaudeCodeHasNoFolder()
    {
        using var elsewhere = new TempDirectory();
        var provider = new ClaudeCodeDerivedStateProvider(
            new FakeUserEnvironment(elsewhere.Path), runner: new FakeProcessRunner(), inspector: FakeProcessInspector.NothingRunning);

        Assert.False(await provider.IsPresentAsync());
    }

    [Fact]
    public async Task OffersOneOfEachKindOfLeftoverAndNothingElse()
    {
        var leftovers = CreateOneOfEachLeftover();

        _claude.Transcript(SessionB);
        _claude.SpilledOutput(SessionB, age: Old);
        _claude.Memory();
        _claude.CreateFile(Path.Combine(_claude.Home, "settings.json"), 64);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            leftovers.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.All(plan.Steps.OfType<DeleteStep>(), step => Assert.True(step.IsLeftover, step.Path));
    }

    /// <summary>
    /// The trap the entry count exists for: every environment folder a session leaves is empty, so it
    /// frees no bytes, and a row that offered nothing on a zero would never offer it at all.
    /// </summary>
    [Fact]
    public async Task AnEmptyEnvironmentFolderIsOfferedOnItsEntryAlone()
    {
        var folder = _claude.HookEnvironment(SessionA, age: Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var step = Assert.Single(plan.Steps);

        Assert.Equal(folder, ((DeleteStep)step).Path);
        Assert.Equal(0, step.EstimatedBytes);
        Assert.Equal(1, step.Estimated.Entries);
        Assert.True(step.RemovesSomething, "an empty leftover could not be chosen");

        var finding = new Finding(provider, IsPresent: true, plan);

        Assert.True(finding.HasSomethingToRemove);
        Assert.True(finding.IsPreSelectedByDefault);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(folder));
        Assert.Equal(1, result.EntriesRemoved);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2's dangerous direction at the top of the folder, including names that are not on the machine
    /// this was measured on. Claude Code adds folders between releases, and failing closed on one it
    /// adds tomorrow is the whole point. Each holds a session-shaped folder, so a rule that reached in
    /// by shape rather than by folder would take it.
    /// </summary>
    [Theory]
    [InlineData("memory")]
    [InlineData("todos")]
    [InlineData("statsig")]
    [InlineData("plugins")]
    [InlineData("commands")]
    [InlineData("agents")]
    [InlineData("skills")]
    [InlineData("rules")]
    [InlineData("agent-memory")]
    [InlineData("workflows")]
    [InlineData("themes")]
    [InlineData("debug")]
    [InlineData("tasks")]
    [InlineData("plans")]
    [InlineData("backups")]
    [InlineData("output-styles")]
    [InlineData("hooks")]
    [InlineData("sessions-archive")]
    public async Task AnUnrecognisedFolderAtTheTopIsTier4AndIsAssertedToSurvive(string name)
    {
        CreateOneOfEachLeftover();

        var folder = Path.Combine(_claude.Home, name);
        _claude.CreateFile(Path.Combine(folder, "keep.bin"), 64);
        var inside = Path.Combine(folder, SessionC);
        Directory.CreateDirectory(inside);
        ClaudeCodeFixture.AgeFolder(inside, Old);

        Assert.Equal(SafetyTier.DoNotTouch, ClaudeCodeDerivedStateProvider.HomeChildren.Classify(name).Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.All(plan.TargetedPaths, path => Assert.False(
            LongPath.Contains(folder, path), $"{path} is inside {name}, which nothing recognises."));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(inside), $"a folder inside {name} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The snapshot folder is declared rather than merely unrecognised: this row names it as the rewind
    /// snapshots row's, never reaches into it, and proves it left it standing. An old session's snapshots
    /// inside are exactly what that other row offers, so a rule here that reached in by shape would take
    /// them.
    /// </summary>
    [Fact]
    public async Task TheRewindSnapshotFolderIsNeverReachedAndIsAssertedWithItsOwnReason()
    {
        CreateOneOfEachLeftover();
        var session = _claude.RewindSnapshots(SessionC, folderAge: Old, snapshotAge: Old);

        var declared = ClaudeCodeDerivedStateProvider.HomeChildren.Classify(ClaudeCodeHome.FileHistory);

        Assert.Equal(SafetyTier.DoNotTouch, declared.Tier);
        Assert.NotEqual(ClaudeCodeDerivedStateProvider.HomeChildren.Classify("not-declared").Reason, declared.Reason);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.Steps);
        Assert.All(plan.TargetedPaths, path => Assert.False(
            LongPath.Contains(_claude.FileHistory, path), $"{path} is inside the snapshot folder."));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(_claude.FileHistory, StringComparison.OrdinalIgnoreCase)
            && p.PresenceBefore is PathPresence.Present
            && p.Reason == declared.Reason);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(session), "an old session's rewind snapshots were removed by the leftovers row");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.2 inside a project folder, where the memory sits beside the sessions.</summary>
    [Theory]
    [InlineData("memory")]
    [InlineData("notes")]
    [InlineData("11111111-1111-4111-8111-11111111111z")]
    public async Task AnUnrecognisedFolderInAProjectFolderIsTier4AndIsAssertedToSurvive(string name)
    {
        _claude.SpilledOutput(SessionA, age: Old);

        var folder = Path.Combine(_claude.Project(), name);
        _claude.CreateFile(Path.Combine(folder, "tool-results", "note.md"), 64);
        ClaudeCodeFixture.AgeFolder(Path.Combine(folder, "tool-results"), Old);
        ClaudeCodeFixture.AgeFolder(folder, Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(folder, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(folder), $"{name} was removed beside the spilled output");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.6's negative, over everything in and beside Claude Code's folder that a run must prove it left:
    /// the sign-in, the configuration beside the folder, the settings, the user's instructions, their
    /// styles and hooks, the configuration backups, a project's memory, the list of running sessions,
    /// and a different program's credentials under a similar name. Four beats each: not targeted,
    /// protected as present before, present on the disk after, and a passing verification.
    ///
    /// <para>A conversation and the output beside it are not asserted by the plan, for the reason
    /// <see cref="NeitherOffersNorAssertsASessionThatStillHasAConversation"/> gives. That makes this test
    /// the only place their survival of a clean is proved, so they are proved here, on the disk.</para>
    /// </summary>
    [Fact]
    public async Task EverythingThatMustSurviveIsAssertedAndDoesSurvive()
    {
        CreateOneOfEachLeftover();

        string[] conversation =
        [
            _claude.Transcript(SessionB),
            _claude.SpilledOutput(SessionB, age: Old),
            _claude.Transcript(SessionC, OtherProjectFolder),
        ];

        string[] mustSurvive =
        [
            _claude.Home,
            _claude.CreateFile(Path.Combine(_claude.Home, ".credentials.json"), 64),
            _claude.CreateFile(Path.Combine(_environment.UserProfile, ".claude.json"), 64),
            _claude.CreateFile(Path.Combine(_claude.Home, "settings.json"), 64),
            _claude.CreateFile(Path.Combine(_claude.Home, "CLAUDE.md"), 64),
            _claude.CreateFile(Path.Combine(_claude.Home, ".last-cleanup"), 8),
            Path.GetDirectoryName(_claude.CreateFile(Path.Combine(_claude.Home, "output-styles", "style.md"), 64))!,
            Path.GetDirectoryName(_claude.CreateFile(Path.Combine(_claude.Home, "hooks", "hook.sh"), 64))!,
            Path.GetDirectoryName(_claude.CreateFile(Path.Combine(_claude.Home, "backups", ".claude.json.backup.1"), 64))!,
            _claude.Memory(),
            _claude.Registered(4199, SessionC),
            Path.GetDirectoryName(_claude.CreateFile(Path.Combine(_environment.UserProfile, ".claude-swap-backup", "blob.bin"), 64))!,
        ];

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.Steps);

        foreach (var path in mustSurvive)
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(plan.TargetedPaths, target => Assert.False(
                LongPath.Contains(target, path), $"{target} would have taken {path} with it."));
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        foreach (var path in mustSurvive.Concat(conversation))
        {
            Assert.True(File.Exists(path) || Directory.Exists(path), $"{path} did not survive the clean.");
        }

        Assert.All(conversation, path => Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A session that still has a conversation is not this provider's to decide about in either
    /// direction. Asserting its transcript or its spilled output would report a §5.6 failure in any run
    /// where a provider that does remove sessions takes them, because a path another step in the run
    /// targeted is read as destroyed by the run.
    /// </summary>
    [Fact]
    public async Task NeitherOffersNorAssertsASessionThatStillHasAConversation()
    {
        CreateOneOfEachLeftover();

        var transcript = _claude.Transcript(SessionB);
        var sidecar = _claude.SpilledOutput(SessionB, age: Old);

        var plan = await CreateProvider().PlanAsync();

        foreach (var path in new[] { transcript, sidecar })
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Whether a session has a transcript is asked of every project folder at once. Measured, session
    /// folders sat in a different project folder from their own transcript, and a set difference taken
    /// one folder at a time would have called their output an orphan.
    /// </summary>
    [Fact]
    public async Task ASessionWhoseTranscriptIsInAnotherProjectFolderIsNotAnOrphan()
    {
        _claude.Transcript(SessionB, OtherProjectFolder);
        var sidecar = _claude.SpilledOutput(SessionB, ProjectFolder, age: Old);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(sidecar, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(sidecar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Spilled tool output is derived from a conversation. A subagent's transcript is a conversation,
    /// so a session folder holding one is not a leftover this provider may take, whatever else is true.
    /// Nor is it asserted, for the reason a session that still has its transcript is not, so its
    /// survival is proved on the disk.
    /// </summary>
    [Fact]
    public async Task ASessionFolderHoldingASubagentsConversationIsLeftForTheUser()
    {
        var sidecar = _claude.SpilledOutput(SessionA, age: Old);
        _claude.CreateFile(Path.Combine(sidecar, "subagents", "agent.jsonl"), 256);
        ClaudeCodeFixture.AgeFolder(Path.Combine(sidecar, "subagents"), Old);
        ClaudeCodeFixture.AgeFolder(sidecar, Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sidecar, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(sidecar, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("subagent", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(sidecar, "subagents", "agent.jsonl")), "a subagent's conversation was removed");
    }

    /// <summary>
    /// A session folder that will not be listed may hold anything, so it is left alone, the plan says it
    /// could not look, and the clean proves the folder is still there like anything else left alone.
    /// </summary>
    [Fact]
    public async Task ASessionFolderThatWillNotBeListedIsLeftAloneAndAssertedToSurvive()
    {
        var sidecar = _claude.SpilledOutput(SessionA, age: Old);

        using var denied = new DeniedDirectory(sidecar);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sidecar, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(sidecar, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.True(plan.HasUnreadableRoot);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(sidecar), "a session folder nobody could list was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2 as §7.1 reads it: Explore may remove exactly what the Storage page offers, and nothing at the
    /// top of the folder, in a project folder, or in the list of running sessions.
    /// </summary>
    [Fact]
    public async Task ExploreMayRemoveWhatIsOfferedAndNothingBesideIt()
    {
        var sidecar = _claude.SpilledOutput(SessionA, age: Old);
        var memory = _claude.Memory();
        var transcript = _claude.Transcript(SessionB);
        var endedLock = _claude.EditorLock(51234, processId: 4101);
        var liveLock = _claude.EditorLock(51235, processId: 4102);
        var registry = _claude.Registered(4199, SessionC);
        var credentials = _claude.CreateFile(Path.Combine(_claude.Home, ".credentials.json"), 64);

        var provider = CreateProvider(FakeProcessInspector.NothingRunning.WithProcess(4102, ProcessLiveness.Running(null)));
        var plan = await provider.PlanAsync();
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        Assert.Contains(sidecar, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(policy.MayRemove(sidecar).IsAllowed, "Explore refused the output it would clean");
        Assert.True(policy.MayRemove(endedLock).IsAllowed, "Explore refused a handshake file it would clean");

        foreach (var refused in new[]
        {
            _claude.Home, _claude.Projects, _claude.Project(), memory, transcript, liveLock, registry, credentials,
            _claude.Ide, _claude.Sessions,
            Path.Combine(_environment.UserProfile, ".claude.json"),
            Path.Combine(_environment.UserProfile, ".claude-swap-backup"),
        })
        {
            Assert.False(policy.MayRemove(refused).IsAllowed, $"Explore would remove {refused}");
        }
    }

    [Fact]
    public async Task FollowsTheConfigDirectoryVariableAndNothingElse()
    {
        _claude.EditorLock(51234, processId: 4101);

        var moved = new ClaudeCodeFixture(Path.Combine(_temp.Path, "elsewhere", "claude-config"));
        var lockThere = moved.EditorLock(51299, processId: 4102);

        _environment.WithEnvironmentVariable(ClaudeCodeHome.ConfigDirectoryVariable, moved.Home);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([lockThere], plan.TargetedPaths);
    }

    [Fact]
    public async Task LeavesEverythingAloneWhereTheConfigDirectoryVariableIsNotAFullPath()
    {
        _claude.EditorLock(51234, processId: 4101);
        _environment.WithEnvironmentVariable(ClaudeCodeHome.ConfigDirectoryVariable, @"relative\claude");

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains(ClaudeCodeHome.ConfigDirectoryVariable, StringComparison.Ordinal));
    }

    /// <summary>
    /// A variable naming the profile or one of the account's own folders as Claude Code's folder. Read
    /// as Claude Code's, every entry there would be asserted as a Claude Code survivor and refused in
    /// Explore as Claude Code's own, and a removal there by any other row would read as a failure.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Downloads")]
    public async Task LeavesEverythingAloneWhereTheConfigDirectoryVariableNamesOneOfTheAccountsOwnFolders(string name)
    {
        var folder = Path.Combine(_environment.UserProfile, name);
        var mine = Path.Combine(folder, "notes.txt");
        Directory.CreateDirectory(folder);
        File.WriteAllText(mine, "mine");
        _environment.WithEnvironmentVariable(ClaudeCodeHome.ConfigDirectoryVariable, folder);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(mine, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("will not treat that as Claude Code's folder", StringComparison.Ordinal));
        Assert.Empty(await provider.DiscoverToolRootsAsync());
    }

    [Fact]
    public async Task AFolderThatIsALinkIsNeverLookedThrough()
    {
        using var elsewhere = new TempDirectory();
        var environment = new FakeUserEnvironment(elsewhere.Path);

        var outside = new ClaudeCodeFixture(Path.Combine(elsewhere.Path, "outside"));
        var bystander = outside.EditorLock(51234, processId: 4101);

        SymbolicLink.ToDirectory(Path.Combine(environment.UserProfile, ".claude"), outside.Home);

        var provider = new ClaudeCodeDerivedStateProvider(
            environment, runner: new FakeProcessRunner(), inspector: FakeProcessInspector.NothingRunning);

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Empty(provider.ToolRoots);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(bystander), "a handshake file was deleted through a linked folder");
    }

    [Fact]
    public async Task AFolderInsideThatIsALinkIsNamedAndNeverFollowed()
    {
        var outside = Path.Combine(_temp.Path, "outside-ide");
        var bystander = _claude.CreateFile(Path.Combine(outside, "51234.lock"), 64);
        SymbolicLink.ToDirectory(_claude.Ide, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(bystander), "a file was deleted through a linked folder");
    }

    /// <summary>
    /// A project folder that will not be listed may hold the transcript some other folder's output
    /// belongs to, so no session anywhere may be called an orphan — and the plan says why rather than
    /// reporting nothing to do.
    /// </summary>
    [Fact]
    public async Task AProjectFolderThatWillNotBeListedStopsEverySessionBeingCalledAnOrphan()
    {
        var sidecar = _claude.SpilledOutput(SessionA, ProjectFolder, age: Old);
        var environmentFolder = _claude.HookEnvironment(SessionA, age: Old);
        var endedLock = _claude.EditorLock(51234, processId: 4101);

        var refused = _claude.Project(OtherProjectFolder);
        _claude.Transcript(SessionB, OtherProjectFolder);

        using var denied = new DeniedDirectory(refused);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(sidecar, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(environmentFolder, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(endedLock, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(refused, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("project folder", StringComparison.Ordinal));
    }

    /// <summary>
    /// G4: the folder is read once for the life of a planning pass, and again after an invalidation, so
    /// an editor that closed while Deguffer was open is seen on the next preview.
    /// </summary>
    [Fact]
    public async Task TheFolderIsReadOncePerPassAndAgainAfterInvalidation()
    {
        var provider = CreateProvider();

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        var endedLock = _claude.EditorLock(51234, processId: 4101);

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        provider.InvalidateCaches();

        Assert.Equal([endedLock], (await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// A handshake file carries the editor's connection token, and this repository's rule is that it is
    /// read for a process id and nothing else. No sentence the plan or its result carries may quote it.
    /// </summary>
    [Fact]
    public async Task NeverQuotesAnEditorsConnectionToken()
    {
        _claude.EditorLock(51234, processId: 4101);
        _claude.EditorLock(51235, processId: 4102);
        _claude.EditorLock(51236, processId: 4103, runningInWindows: false);

        var provider = CreateProvider(FakeProcessInspector.NothingRunning.WithProcess(4102, ProcessLiveness.Running(null)));
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        var sentences = plan.Notes.Select(n => n.Message)
            .Concat(plan.Steps.Select(s => s.Description))
            .Concat(plan.ProtectedPaths.Select(p => p.Reason))
            .Concat(result.Steps.Select(s => s.Message ?? string.Empty));

        Assert.All(sentences, sentence => Assert.DoesNotContain(AuthToken, sentence, StringComparison.OrdinalIgnoreCase));
    }
}
