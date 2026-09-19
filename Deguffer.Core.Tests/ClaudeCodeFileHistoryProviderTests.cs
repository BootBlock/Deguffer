using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;
using static Deguffer.Core.Tests.Fakes.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// Claude Code's rewind snapshots: one folder per session, offered at Tier 3 once nothing lists the
/// session as running and nothing has written to its folder for a week.
///
/// <para>The trap these exist for was measured. A snapshot keeps the last-write time of the file it
/// copied, months older than the folder holding it, so a session is dated by its folder and never by
/// its files.</para>
///
/// <para>Everything runs against an invented folder through <see cref="FakeUserEnvironment"/> and
/// <see cref="FakeProcessInspector"/>. No test needs Claude Code installed, and none reads a real
/// one.</para>
/// </summary>
public sealed class ClaudeCodeFileHistoryProviderTests : IDisposable
{
    private const int SessionProcess = 4242;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ClaudeCodeFixture _claude;

    public ClaudeCodeFileHistoryProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _claude = new ClaudeCodeFixture(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private ClaudeCodeFileHistoryProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, runner: new FakeProcessRunner(), inspector: inspector ?? FakeProcessInspector.NothingRunning);

    private static FakeProcessInspector Answering(ProcessState state) => state switch
    {
        ProcessState.Running => FakeProcessInspector.NothingRunning.WithProcess(SessionProcess, ProcessLiveness.Running(null)),
        ProcessState.Undetermined => FakeProcessInspector.NothingRunning.WithProcess(SessionProcess, ProcessLiveness.Undetermined),
        _ => FakeProcessInspector.NothingRunning,
    };

    private string OldSession(string session) => _claude.RewindSnapshots(session, folderAge: Old, snapshotAge: Old);

    private static void AssertKept(CleanupPlan plan, string path)
    {
        Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    [Fact]
    public async Task ReportsNotPresentWhereClaudeCodeHasTakenNoSnapshots()
    {
        _claude.HookEnvironment(SessionA, age: Old);

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>One step per session, at Tier 3, and never chosen for the user.</summary>
    [Fact]
    public async Task OffersEachEndedSessionsSnapshotsAtTier3AndNeverChoosesThem()
    {
        string[] sessions = [OldSession(SessionA), OldSession(SessionB)];

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            sessions.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.UserData, plan.Tier);
        Assert.Equal(2, plan.Steps.Count);
        Assert.All(plan.Steps.OfType<DeleteStep>(), step => Assert.False(step.IsLeftover, step.Path));
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);

        var result = await provider.ExecuteAsync(plan);

        Assert.All(sessions, session => Assert.False(Directory.Exists(session), $"{session} was not removed"));
        Assert.True(Directory.Exists(_claude.FileHistory), "the snapshot folder itself was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The measured trap: every snapshot carries the date of the file it copied, five months old here,
    /// in a folder a session wrote to today. Dated by its files, this session would be offered.
    /// </summary>
    [Fact]
    public async Task ASessionIsDatedByItsFolderAndNeverByTheOldSnapshotsInIt()
    {
        var session = _claude.RewindSnapshots(SessionA, folderAge: null, snapshotAge: TimeSpan.FromDays(150));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(session, StringComparison.OrdinalIgnoreCase)
            && p.PresenceBefore is PathPresence.Present
            && p.Withheld == Withholding.TooRecent);
        Assert.True(plan.HasRecentContentHeldBack);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(session), "a session written to today was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The other half of the same rule. A snapshot rewritten in place moves no folder timestamp, so a
    /// folder that reads old can still hold this week's work.
    /// </summary>
    [Fact]
    public async Task AnOldFolderHoldingASnapshotWrittenThisWeekIsHeldBack()
    {
        var session = _claude.RewindSnapshots(SessionA, folderAge: Old, snapshotAge: TimeSpan.FromHours(1));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(session, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.TooRecent);
    }

    /// <summary>
    /// The floor travels on the plan, not only into the preview. A session resumed between the preview and
    /// the clean writes new snapshots into a folder the preview offered. Each is a copy, so its last-write
    /// time is its source file's and only its creation time is new. The removal spares it, and the folder
    /// holding it stands.
    /// </summary>
    [Fact]
    public async Task ASnapshotWrittenAfterThePreviewIsSparedByTheClean()
    {
        var session = OldSession(SessionA);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([session], plan.TargetedPaths);

        var resumed = _claude.CreateFile(Path.Combine(session, "fedcba9876543210@v1"), 256);
        File.SetLastWriteTimeUtc(LongPath.Extended(resumed), DateTime.UtcNow.AddDays(-150));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(resumed), "a snapshot taken after the preview was removed");
        Assert.True(Directory.Exists(session), "the folder of a resumed session was removed");
        Assert.False(File.Exists(Path.Combine(session, "0123456789abcdef@v1")), "the old snapshots were left behind");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A session Claude Code lists is released only once its process has been asked about and has
    /// ended. A process nothing could ask about is not one that has ended.
    /// </summary>
    [Theory]
    [InlineData(ProcessState.NotRunning, true)]
    [InlineData(ProcessState.Running, false)]
    [InlineData(ProcessState.Undetermined, false)]
    public async Task ASessionListedAsRunningIsOfferedOnlyOnceItsProcessHasEnded(ProcessState state, bool offered)
    {
        var session = OldSession(SessionA);
        _claude.Registered(SessionProcess, SessionA);

        var provider = CreateProvider(Answering(state));
        var plan = await provider.PlanAsync();

        if (offered)
        {
            Assert.Equal([session], plan.TargetedPaths);
            return;
        }

        AssertKept(plan, session);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(session), "a running session's snapshots were removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// "Nothing is running" and "we could not tell" lead to opposite decisions. A list with an entry
    /// nobody could read refuses every session, and says so.
    /// </summary>
    [Fact]
    public async Task AListThatCannotBeReadRefusesEverySession()
    {
        string[] sessions = [OldSession(SessionA), OldSession(SessionB)];
        _claude.WriteText(Path.Combine(_claude.Sessions, $"{SessionProcess}.json"), "{ not an entry");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);

        foreach (var session in sessions)
        {
            AssertKept(plan, session);
        }

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("list of running sessions", StringComparison.Ordinal));
    }

    /// <summary>
    /// A folder that will not be listed has no age. Its own timestamp alone is the half that reads older
    /// than the truth, so it is never offered on that.
    /// </summary>
    [Fact]
    public async Task ASessionFolderThatCannotBeDatedIsNeverOffered()
    {
        var session = OldSession(SessionA);

        using var denied = new DeniedDirectory(session);

        AssertKept(await CreateProvider().PlanAsync(), session);
    }

    /// <summary>
    /// §5.2 inside the snapshot folder, including names shaped almost like a session's. Only a folder
    /// named for a session is recognised.
    /// </summary>
    [Theory]
    [InlineData("not-a-session", true)]
    [InlineData(SessionA + ".bak", true)]
    [InlineData("backups", true)]
    [InlineData(SessionB, false)]
    [InlineData("index.json", false)]
    public async Task AnythingInTheSnapshotFolderNotNamedForASessionIsTier4AndSurvives(string name, bool isFolder)
    {
        OldSession(SessionC);

        var path = Path.Combine(_claude.FileHistory, name);

        if (isFolder)
        {
            var inside = _claude.CreateFile(Path.Combine(path, "0123456789abcdef@v1"), 64);
            TempDirectory.Age(inside, Old);
            AgeFolder(path, Old);
        }
        else
        {
            _claude.CreateFile(path, 64);
            TempDirectory.Age(path, Old);
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.Steps);
        AssertKept(plan, path);

        // Kept by the rule that does not recognise it, and not by a later one that happens to refuse it: a
        // file named like a session reaches the dating step without that rule, and is refused as undated.
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
            && p.Reason == ClaudeCodeClassificationBuilder.UnrecognisedReason);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(path) || Directory.Exists(path), $"{name} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2 at the top of Claude Code's folder, including names that are not on the machine this was
    /// measured on. Each holds a folder named for a session, so a rule that reached in by shape rather
    /// than by place would take it.
    /// </summary>
    [Theory]
    [InlineData("projects")]
    [InlineData("session-env")]
    [InlineData("todos")]
    [InlineData("plans")]
    [InlineData("backups")]
    [InlineData("file-history-archive")]
    public async Task NothingOutsideTheSnapshotFolderIsEverReached(string name)
    {
        var offered = OldSession(SessionA);

        var elsewhere = Path.Combine(_claude.Home, name, SessionB);
        var inside = _claude.CreateFile(Path.Combine(elsewhere, "0123456789abcdef@v1"), 64);
        TempDirectory.Age(inside, Old);
        AgeFolder(elsewhere, Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([offered], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(elsewhere), $"a session-shaped folder in {name} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.6's negative over what this row must prove it left: Claude Code's folder, the snapshot folder,
    /// a running session's snapshots and a recent session's. Four beats each: not targeted, protected as
    /// present before, present on the disk after, and a passing verification.
    /// </summary>
    [Fact]
    public async Task EverythingThisRowMustLeaveIsAssertedAndDoesSurvive()
    {
        OldSession(SessionA);

        var running = OldSession(SessionB);
        _claude.Registered(SessionProcess, SessionB);

        string[] mustSurvive = [_claude.Home, _claude.FileHistory, running, _claude.RewindSnapshots(SessionC)];

        var provider = CreateProvider(Answering(ProcessState.Running));
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
            Assert.True(Directory.Exists(path), $"{path} did not survive the clean.");
        }

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The scope of that negative. This row asserts nothing in Claude Code's folder outside the snapshot
    /// folder, because the leftovers row removes some of it, and in one run a path another row removed
    /// reads as destroyed by the run. Both rows run here against one list of running sessions, and both
    /// verify.
    /// </summary>
    [Fact]
    public async Task ARunWithTheLeftoversRowVerifiesBoth()
    {
        var snapshots = OldSession(SessionA);
        var environmentFolder = _claude.HookEnvironment(SessionA, age: Old);

        var sessions = new ClaudeCodeSessionRegistry(_environment, FakeProcessInspector.NothingRunning);
        var leftovers = new ClaudeCodeDerivedStateProvider(
            _environment, sessions, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);
        var rewind = new ClaudeCodeFileHistoryProvider(
            _environment, sessions, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

        var leftoverPlan = await leftovers.PlanAsync();
        var rewindPlan = await rewind.PlanAsync();
        var reach = RunReach.Of([leftoverPlan, rewindPlan]);

        Assert.Equal([environmentFolder], leftoverPlan.TargetedPaths);
        Assert.Equal([snapshots], rewindPlan.TargetedPaths);

        var first = await leftovers.ExecuteAsync(leftoverPlan, reach);
        var second = await rewind.ExecuteAsync(rewindPlan, reach);

        Assert.False(Directory.Exists(environmentFolder));
        Assert.False(Directory.Exists(snapshots));
        Assert.True(first.Verification!.Passed, first.Verification.Summary);
        Assert.True(second.Verification!.Passed, second.Verification.Summary);
    }

    /// <summary>
    /// §5.2 as §7.1 reads it: Explore may remove exactly the sessions the Storage page offers, and
    /// nothing beside them.
    /// </summary>
    [Fact]
    public async Task ExploreMayRemoveWhatIsOfferedAndNothingBesideIt()
    {
        var offered = OldSession(SessionA);
        var running = OldSession(SessionB);
        _claude.Registered(SessionProcess, SessionB);

        var unrecognised = Path.GetDirectoryName(
            _claude.CreateFile(Path.Combine(_claude.FileHistory, "notes", "note.md"), 64))!;

        var provider = CreateProvider(Answering(ProcessState.Running));
        await provider.PlanAsync();

        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        Assert.True(policy.MayRemove(offered).IsAllowed, "Explore refused a session the Storage page would clean");

        foreach (var refused in new[] { _claude.Home, _claude.FileHistory, running, unrecognised, _claude.Projects })
        {
            Assert.False(policy.MayRemove(refused).IsAllowed, $"Explore would remove {refused}");
        }
    }

    /// <summary>
    /// A session folder that is a link is named and refused, never dated through or offered. The link and
    /// what it points at are both aged, so the recency floor cannot be what keeps it out of the plan.
    /// </summary>
    [Fact]
    public async Task ASessionFolderThatIsALinkIsNamedAndNeverFollowed()
    {
        var outside = Path.Combine(_temp.Path, "outside-snapshots");
        var bystander = _claude.CreateFile(Path.Combine(outside, "0123456789abcdef@v1"), 64);
        TempDirectory.Age(bystander, Old);

        Directory.CreateDirectory(_claude.FileHistory);
        var link = Path.Combine(_claude.FileHistory, SessionA);
        Directory.CreateSymbolicLink(link, outside);
        AgeFolder(link, Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasRecentContentHeldBack);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase)
            && p.PresenceBefore is PathPresence.Present
            && p.Withheld == Withholding.None
            && p.Reason == CacheLevelWalk.LinkReason);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(LongPath.IsReparsePoint(link), "the linked session folder was removed");
        Assert.True(File.Exists(bystander), "a snapshot was deleted through a linked session folder");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A snapshot folder, or a home, Windows will not describe is declared all the same. The plan
    /// names it as unreached, and the declaration dropped it, so Explore offered a session's snapshots
    /// from a folder the Storage page said nothing was ruled out in. Nothing established which
    /// sessions are running, so none of them may go.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExploreRefusesAllOfASnapshotFolderWindowsWillNotDescribe(bool refuseTheHome)
    {
        var session = OldSession(SessionA);

        using var denied = DeniedDirectory.WithUnreadableAttributes(refuseTheHome ? _claude.Home : _claude.FileHistory);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);

        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        foreach (var refused in new[] { _claude.Home, _claude.FileHistory, session })
        {
            Assert.False(policy.MayRemove(refused).IsAllowed, $"Explore would remove {refused}");
        }
    }

    [Fact]
    public async Task ASnapshotFolderThatIsALinkIsNeverLookedThrough()
    {
        var outside = new ClaudeCodeFixture(Path.Combine(_temp.Path, "outside"));
        var bystander = outside.RewindSnapshots(SessionA, folderAge: Old, snapshotAge: Old);

        Directory.CreateSymbolicLink(_claude.FileHistory, outside.FileHistory);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Empty(provider.ToolRoots);

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(bystander), "a session's snapshots were deleted through a linked folder");
    }

    [Fact]
    public async Task ASnapshotFolderThatWillNotBeListedOffersNothingAndSaysSo()
    {
        OldSession(SessionA);

        using var denied = new DeniedDirectory(_claude.FileHistory);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(_claude.FileHistory, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    [Fact]
    public async Task FollowsTheConfigDirectoryVariableAndNothingElse()
    {
        OldSession(SessionA);

        var moved = new ClaudeCodeFixture(Path.Combine(_temp.Path, "elsewhere", "claude-config"));
        var there = moved.RewindSnapshots(SessionB, folderAge: Old, snapshotAge: Old);

        _environment.WithEnvironmentVariable(ClaudeCodeHome.ConfigDirectoryVariable, moved.Home);

        Assert.Equal([there], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    [Fact]
    public async Task LeavesEverythingAloneWhereTheConfigDirectoryVariableIsNotAFullPath()
    {
        OldSession(SessionA);
        _environment.WithEnvironmentVariable(ClaudeCodeHome.ConfigDirectoryVariable, @"relative\claude");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains(ClaudeCodeHome.ConfigDirectoryVariable, StringComparison.Ordinal));
    }

    /// <summary>
    /// G4: the folder and the list of running sessions are read once for the life of a planning pass, and
    /// again after an invalidation, so a session that ended while Deguffer was open is seen on the next
    /// preview.
    /// </summary>
    [Fact]
    public async Task TheListIsReadOncePerPassAndAgainAfterInvalidation()
    {
        var session = OldSession(SessionA);
        var entry = _claude.Registered(SessionProcess, SessionA);

        var provider = CreateProvider(Answering(ProcessState.Running));

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        File.Delete(entry);

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        provider.InvalidateCaches();

        Assert.Equal([session], (await provider.PlanAsync()).TargetedPaths);
    }
}
