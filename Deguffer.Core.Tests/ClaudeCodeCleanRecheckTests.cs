using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;
using static Deguffer.Core.Tests.Fakes.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the two Claude Code rows decided at Preview, asked again at Clean. A preview can sit on screen
/// indefinitely, and a session resumed or an editor started in that time makes the preview's answer
/// wrong in the direction that deletes.
///
/// <para>Each test plans, changes the machine the way a user would between the two presses, and then
/// cleans. Everything runs against an invented folder through <see cref="FakeUserEnvironment"/> and
/// <see cref="FakeProcessInspector"/>.</para>
/// </summary>
public sealed class ClaudeCodeCleanRecheckTests : IDisposable
{
    private const int ResumedProcess = 4242;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ClaudeCodeFixture _claude;
    private readonly FakeProcessInspector _inspector = FakeProcessInspector.NothingRunning;

    public ClaudeCodeCleanRecheckTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _claude = new ClaudeCodeFixture(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private ClaudeCodeFileHistoryProvider RewindRow() =>
        new(_environment, runner: new FakeProcessRunner(), inspector: _inspector);

    private ClaudeCodeDerivedStateProvider LeftoversRow() =>
        new(_environment, runner: new FakeProcessRunner(), inspector: _inspector);

    /// <summary>The user resumes <paramref name="session"/> while the preview is on screen.</summary>
    private void Resume(string session)
    {
        _claude.Registered(ResumedProcess, session);
        _inspector.WithProcess(ResumedProcess, ProcessLiveness.Running(null));
    }

    /// <summary>
    /// The case the issue names. A session nothing listed at the preview is resumed before the clean.
    /// Its id is the same, so it may still rewind to the snapshots the preview offered, and the clean
    /// leaves them. The other session's snapshots still go.
    /// </summary>
    [Fact]
    public async Task ASessionResumedAfterThePreviewKeepsItsRewindSnapshots()
    {
        var resumed = _claude.RewindSnapshots(SessionA, folderAge: Old, snapshotAge: Old);
        var ended = _claude.RewindSnapshots(SessionB, folderAge: Old, snapshotAge: Old);

        var provider = RewindRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(resumed, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        Resume(SessionA);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(resumed), "the snapshots of a session resumed after the preview were removed");
        Assert.False(Directory.Exists(ended), "the snapshots of a session that stayed ended were kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: the Claude Code session it belongs to is running again.");
        AssertProvedStanding(result, resumed);
    }

    /// <summary>
    /// A list that cannot be read at the clean is not an answer, and the plan would have offered
    /// nothing named by a session on it. The clean holds the step back for the same reason.
    /// </summary>
    [Fact]
    public async Task ASessionListThatCannotBeReadAtTheCleanHoldsTheSnapshotsBack()
    {
        var session = _claude.RewindSnapshots(SessionA, folderAge: Old, snapshotAge: Old);

        var provider = RewindRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(session, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        _claude.WriteText(Path.Combine(_claude.Sessions, "5150.json"), "not json");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(session), "snapshots were removed on a list nobody could read");
        Assert.StartsWith(
            "Nothing was removed: Deguffer could not read Claude Code's list of running sessions",
            Assert.Single(result.Steps).Message,
            StringComparison.Ordinal);
        AssertProvedStanding(result, session);
    }

    /// <summary>
    /// Everything the leftovers row offers by a session's name is asked about again: the hook
    /// environment, the spilled output and the unsent events of a session that is running again all
    /// stay. So does a shell capture a running session may have made, which names no session.
    /// </summary>
    [Fact]
    public async Task EveryLeftoverNamedByAResumedSessionStays()
    {
        var environment = _claude.HookEnvironment(SessionA, age: Old);
        var events = _claude.FailedEvents(SessionA, age: Old);
        var otherEvents = _claude.FailedEvents(SessionB, age: Old);

        var provider = LeftoversRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(environment, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(events, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        Resume(SessionA);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(environment), "a resumed session's hook environment was removed");
        Assert.True(File.Exists(events), "a resumed session's unsent events were removed");
        Assert.False(File.Exists(otherEvents), "an ended session's unsent events were kept");
        AssertProvedStanding(result, environment);
        AssertProvedStanding(result, events);
    }

    /// <summary>
    /// A shell capture names no session, so it is offered once it predates every running session. A
    /// session from another system has no start this machine can read, so it could have made anything,
    /// and one that appears after the preview holds the capture back.
    /// </summary>
    [Fact]
    public async Task AShellCaptureStaysWhenASessionThatMayHaveMadeItAppearsAfterThePreview()
    {
        var capture = _claude.ShellSnapshot(DateTime.UtcNow - Old);

        var provider = LeftoversRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(capture, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        _claude.Registered(ResumedProcess, SessionC, domain: "linux:elsewhere");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(capture), "a capture a running session may have made was removed");
        AssertProvedStanding(result, capture);
    }

    /// <summary>
    /// The other gap the issue names. A handshake file is named by port, and it is offered once the
    /// editor that wrote it has closed, however recently. An editor that starts while the preview is on
    /// screen and takes the same port writes the same file, and the clean must not delete the live
    /// editor's copy. The removal keeps anything written after the evidence was read.
    /// </summary>
    [Fact]
    public async Task AHandshakeFileAnEditorRewroteAfterThePreviewSurvivesTheClean()
    {
        var rewritten = _claude.EditorLock(51234, processId: 4101);
        var stale = _claude.EditorLock(51235, processId: 4102);
        TempDirectory.Age(rewritten, Old);
        TempDirectory.Age(stale, Old);

        var provider = LeftoversRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(rewritten, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(stale, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        _claude.EditorLock(51234, processId: 4999);
        _inspector.WithProcess(4999, ProcessLiveness.Running(null));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(rewritten), "the handshake file of an editor started after the preview was removed");
        Assert.False(File.Exists(stale), "the handshake file of an editor that stayed closed was kept");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The guard is anchored at the evidence rather than a window, so it keeps nothing the preview
    /// offered. A window of any length would keep every handshake file whose editor closed inside it.
    /// </summary>
    [Fact]
    public async Task TheGuardOnTheLeftoversKeepsNothingThePreviewOffered()
    {
        var closedAMinuteAgo = _claude.EditorLock(51234, processId: 4101);

        var plan = await LeftoversRow().PlanAsync();

        Assert.Contains(closedAMinuteAgo, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.Keep.IsOn);
        Assert.False(plan.HasRecentContentHeldBack);
        Assert.DoesNotContain(plan.Notes, note => note.Message.Contains("changed", StringComparison.Ordinal));
    }

    private static void AssertProvedStanding(CleanupResult result, string path)
    {
        var check = Assert.Single(result.Verification!.Checks, c => c.Subject.Equals(path, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(VerificationOutcome.Survived, check.Outcome);
        Assert.True(result.Verification.Passed, result.Verification.Summary);
    }
}
