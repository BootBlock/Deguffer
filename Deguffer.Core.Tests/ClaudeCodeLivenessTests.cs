using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;
using static Deguffer.Core.Tests.Fakes.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether the thing a Claude Code leftover belongs to has ended, which decides whether it is offered
/// at all.
///
/// <para>Three kinds of evidence, and each fails in its own direction. A file that names a process is
/// offered once that process has been asked about and has ended, and only then. Anything that names a
/// session is offered only where Claude Code's list of running sessions does not list it, and never
/// while that list cannot be read. Everything that names no process is held back for a week as well,
/// because a version of Claude Code older than the list is invisible to it.</para>
///
/// <para>The processes are declared through <see cref="FakeProcessInspector"/>, so running, ended and
/// could-not-tell each reach the provider without a real process.</para>
/// </summary>
public sealed class ClaudeCodeLivenessTests : IDisposable
{
    private const int SessionProcess = 4242;

    /// <summary>When the running session's process was created: ten days ago, to the second.</summary>
    private static readonly DateTimeOffset Started = new(
        DateTimeOffset.UtcNow.AddDays(-10).UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
        TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ClaudeCodeFixture _claude;

    public ClaudeCodeLivenessTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _claude = new ClaudeCodeFixture(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private ClaudeCodeDerivedStateProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, runner: new FakeProcessRunner(), inspector: inspector ?? FakeProcessInspector.NothingRunning);

    private static FakeProcessInspector Running(int processId, DateTimeOffset? started) =>
        FakeProcessInspector.NothingRunning.WithProcess(processId, ProcessLiveness.Running(started));

    private static FakeProcessInspector Answering(int processId, ProcessState state) => state switch
    {
        ProcessState.Running => Running(processId, started: null),
        ProcessState.Undetermined => FakeProcessInspector.NothingRunning.WithProcess(processId, ProcessLiveness.Undetermined),
        _ => FakeProcessInspector.NothingRunning,
    };

    /// <summary>Everything a session leaves that names it, each old enough to offer.</summary>
    private string[] LeftoversOf(string session) =>
    [
        _claude.SpilledOutput(session, age: Old),
        _claude.HookEnvironment(session, age: Old),
        _claude.FailedEvents(session, age: Old),
    ];

    private static void AssertKept(CleanupPlan plan, string path)
    {
        Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    [Fact]
    public async Task ASessionListedAsRunningKeepsEverythingOfItsOwn()
    {
        var leftovers = LeftoversOf(SessionA);
        _claude.Registered(SessionProcess, SessionA, Started);

        var provider = CreateProvider(Running(SessionProcess, Started));
        var plan = await provider.PlanAsync();

        foreach (var path in leftovers)
        {
            AssertKept(plan, path);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.All(leftovers, path => Assert.True(
            File.Exists(path) || Directory.Exists(path), $"{path} was removed while its session was running."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task AListedProcessThatHasEndedReleasesItsSession()
    {
        var leftovers = LeftoversOf(SessionA);
        _claude.Registered(SessionProcess, SessionA, Started);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            leftovers.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>A process nothing could ask about is not a process that has ended.</summary>
    [Fact]
    public async Task AListedProcessNothingCouldAskAboutKeepsItsSession()
    {
        var leftovers = LeftoversOf(SessionA);
        _claude.Registered(SessionProcess, SessionA, Started);

        var plan = await CreateProvider(Answering(SessionProcess, ProcessState.Undetermined)).PlanAsync();

        foreach (var path in leftovers)
        {
            AssertKept(plan, path);
        }
    }

    /// <summary>
    /// An id held by a process created after the one the list recorded has passed to a stranger: the
    /// session the list names has ended, however long the stranger runs.
    /// </summary>
    [Fact]
    public async Task AnIdThatPassedToALaterProcessDoesNotKeepAnEndedSession()
    {
        var leftovers = LeftoversOf(SessionA);
        _claude.Registered(SessionProcess, SessionA, Started);

        var plan = await CreateProvider(Running(SessionProcess, Started.AddHours(1))).PlanAsync();

        Assert.Equal(
            leftovers.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An entry written inside WSL, or on another machine sharing the folder, names an id this machine's
    /// process table did not issue. Asking about it would ask about a stranger.
    /// </summary>
    [Fact]
    public async Task AListedProcessFromAnotherSystemKeepsItsSession()
    {
        var leftovers = LeftoversOf(SessionA);
        _claude.Registered(SessionProcess, SessionA, Started, domain: "win32:another-machine");

        var plan = await CreateProvider().PlanAsync();

        foreach (var path in leftovers)
        {
            AssertKept(plan, path);
        }
    }

    /// <summary>
    /// "Nothing is running" and "we could not tell" lead to opposite decisions. A list with an entry
    /// nobody could read refuses everything that names a session, and says so. A file that names a
    /// process is still asked about directly, because its evidence never came from the list.
    /// </summary>
    [Fact]
    public async Task AListThatCannotBeReadRefusesEverySessionsLeftovers()
    {
        var leftovers = LeftoversOf(SessionA).Append(_claude.ShellSnapshot(DateTime.UtcNow - Old)).ToArray();
        _claude.WriteText(Path.Combine(_claude.Sessions, $"{SessionProcess}.json"), "{ not an entry");

        var endedLock = _claude.EditorLock(51234, processId: 4101);
        var endedKey = _claude.MessagingKey(4102);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            new[] { endedLock, endedKey }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var path in leftovers)
        {
            AssertKept(plan, path);
        }

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("list of running sessions", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing written in the last week is offered, whatever the list says, and a clean proves each of
    /// them still there. The row must not read as clear while they are on the disk.
    /// </summary>
    [Fact]
    public async Task RecentLeftoversAreHeldBackAndProvedToSurvive()
    {
        string[] recent =
        [
            _claude.SpilledOutput(SessionA),
            _claude.HookEnvironment(SessionA),
            _claude.FailedEvents(SessionA),
            _claude.ShellSnapshot(DateTime.UtcNow.AddHours(-1)),
        ];

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasRecentContentHeldBack);
        Assert.Contains(plan.Notes, n => n.Message.Contains("last 7 days", StringComparison.Ordinal));

        foreach (var path in recent)
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                && p.ExistedBefore
                && p.Withheld == Withholding.TooRecent);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.All(recent, path => Assert.True(File.Exists(path) || Directory.Exists(path), $"{path} was removed"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The measured trap for anything Claude Code copies into a session's folders: a copied file keeps
    /// the date of the file it was copied from, months older than the copy. A folder in use holding
    /// only such files must still read as in use.
    /// </summary>
    [Fact]
    public async Task AFolderIsDatedByItselfAndNeverByTheOldFilesInIt()
    {
        var sidecar = _claude.SpilledOutput(SessionA);
        TempDirectory.Age(Path.Combine(sidecar, "tool-results", "output.txt"), TimeSpan.FromDays(150));

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(sidecar, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(sidecar, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.TooRecent);
    }

    [Theory]
    [InlineData(ProcessState.NotRunning, true)]
    [InlineData(ProcessState.Running, false)]
    [InlineData(ProcessState.Undetermined, false)]
    public async Task AHandshakeFileIsOfferedOnlyOnceItsEditorHasEnded(ProcessState state, bool offered)
    {
        var handshake = _claude.EditorLock(51234, processId: 4101);

        var plan = await CreateProvider(Answering(4101, state)).PlanAsync();

        if (offered)
        {
            Assert.Equal([handshake], plan.TargetedPaths);
        }
        else
        {
            AssertKept(plan, handshake);
        }
    }

    /// <summary>
    /// An editor running anywhere but Windows records an id this machine's process table did not issue,
    /// and a free id here says nothing about it.
    /// </summary>
    [Fact]
    public async Task AHandshakeFileWrittenOutsideWindowsIsNeverOffered()
    {
        var handshake = _claude.EditorLock(51234, processId: 4101, runningInWindows: false);

        AssertKept(await CreateProvider().PlanAsync(), handshake);
    }

    [Fact]
    public async Task AHandshakeFileThatCannotBeReadIsNeverOffered()
    {
        var handshake = _claude.WriteText(Path.Combine(_claude.Ide, "51234.lock"), "not a handshake");

        AssertKept(await CreateProvider().PlanAsync(), handshake);
    }

    [Theory]
    [InlineData(ProcessState.NotRunning, true)]
    [InlineData(ProcessState.Running, false)]
    [InlineData(ProcessState.Undetermined, false)]
    public async Task AMessagingKeyIsOfferedOnlyOnceItsProcessHasEnded(ProcessState state, bool offered)
    {
        var key = _claude.MessagingKey(4102);

        var plan = await CreateProvider(Answering(4102, state)).PlanAsync();

        if (offered)
        {
            Assert.Equal([key], plan.TargetedPaths);
        }
        else
        {
            AssertKept(plan, key);
        }
    }

    [Fact]
    public async Task AKeyWrittenInAnotherProcessNamespaceIsNeverOffered()
    {
        var key = _claude.MessagingKey(4102, domain: "win32:0123456789abcdef:4026531836");

        AssertKept(await CreateProvider().PlanAsync(), key);
    }

    /// <summary>The same machine's own domain is asked about, so the rule above is not simply "never".</summary>
    [Fact]
    public async Task AKeyWrittenOnThisMachineIsAskedAbout()
    {
        var key = _claude.MessagingKey(4102, domain: "win32:testmachine");

        Assert.Equal([key], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    [Fact]
    public async Task AKeyWhoseIdPassedToALaterProcessIsOffered()
    {
        var key = _claude.MessagingKey(4102, started: Started);

        var plan = await CreateProvider(Running(4102, Started.AddHours(1))).PlanAsync();

        Assert.Equal([key], plan.TargetedPaths);
    }

    [Fact]
    public async Task AKeyWhoseRecordedStartIsTheRunningProcessIsKept()
    {
        var key = _claude.MessagingKey(4102, started: Started);

        AssertKept(await CreateProvider(Running(4102, Started)).PlanAsync(), key);
    }

    /// <summary>
    /// A shell capture names no session, so time is the only evidence: nothing a running session made can
    /// predate that session's process. One taken afterwards may be in use, however old it now is.
    /// </summary>
    [Fact]
    public async Task AShellCaptureIsOfferedOnlyWhenItPredatesEveryRunningSession()
    {
        _claude.Registered(SessionProcess, SessionA, Started);

        var before = _claude.ShellSnapshot(Started.UtcDateTime.AddDays(-5), "before");
        var after = _claude.ShellSnapshot(Started.UtcDateTime.AddDays(2), "after");

        var plan = await CreateProvider(Running(SessionProcess, Started)).PlanAsync();

        Assert.Equal([before], plan.TargetedPaths);
        AssertKept(plan, after);
    }

    /// <summary>One running session whose start nobody could read may be the earliest of them all.</summary>
    [Fact]
    public async Task NoShellCaptureIsOfferedWhileARunningSessionsStartIsUnknown()
    {
        _claude.Registered(SessionProcess, SessionA, started: null);
        var capture = _claude.ShellSnapshot(DateTime.UtcNow.AddDays(-60));

        AssertKept(await CreateProvider(Running(SessionProcess, started: null)).PlanAsync(), capture);
    }

    [Fact]
    public async Task UnsentEventsAreKeptWhileTheirSessionRunsAndOfferedOnceItHasEnded()
    {
        var running = _claude.FailedEvents(SessionA, age: Old);
        var ended = _claude.FailedEvents(SessionB, age: Old);
        _claude.Registered(SessionProcess, SessionA, Started);

        var plan = await CreateProvider(Running(SessionProcess, Started)).PlanAsync();

        Assert.Equal([ended], plan.TargetedPaths);
        AssertKept(plan, running);
    }

    /// <summary>
    /// §5.2 inside each folder a leftover is recognised in, including names shaped almost like one. A
    /// name the rule does not recognise is Tier 4 whatever it resembles.
    /// </summary>
    [Theory]
    [InlineData("ide", "51234.lock.bak")]
    [InlineData("ide", "notes.txt")]
    [InlineData("sessions", "4101.abcdef.key")]
    [InlineData("sessions", "4101.json.tmp")]
    [InlineData("shell-snapshots", "snapshot-bash-notatime-abc.sh")]
    [InlineData("shell-snapshots", "my-script.sh")]
    [InlineData("telemetry", "flat-migration.one.two")]
    [InlineData("session-env", "not-a-session")]
    public async Task AnythingUnrecognisedInAKnownFolderIsTier4AndSurvives(string folder, string name)
    {
        var path = _claude.CreateFile(Path.Combine(_claude.Home, folder, name), 64);
        TempDirectory.Age(path, Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        AssertKept(plan, path);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(path), $"{folder}\\{name} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }
}
