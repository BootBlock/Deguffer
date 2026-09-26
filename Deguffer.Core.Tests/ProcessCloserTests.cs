using Deguffer.Core.Execution;
using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.2.1's one action, end to end: decide again under the handle, post to the windows that are
/// still the target's, watch with no deadline, and report what §5.6 established.
///
/// <para>Every process, window and figure here is invented. What this proves cannot be proven
/// against the real machine: a process cannot be made to hand its identifier on between two calls,
/// a window cannot be made to change owner between the survey and the post, and the desktop cannot
/// be made to lose its shell.</para>
/// </summary>
public sealed class ProcessCloserTests
{
    private const int Shell = 100;
    private const int Compositor = 120;
    private const int Own = 900;
    private const int TargetId = 4321;
    private const int Child = 4400;
    private const int Host = 500;

    private const nint TargetWindow = 0x0011;
    private const nint SecondWindow = 0x0012;
    private const nint ShellWindow = 0x0099;

    private const long ShellCreated = 1;
    private const long CompositorCreated = 2;
    private const long OwnCreated = 3;
    private const long HostCreated = 4;
    private const long TargetCreated = 5;
    private const long ChildCreated = 6;

    /// <summary>The machine as the close is asked for: a desktop, Deguffer, a host, and the target.</summary>
    private static MemorySnapshot Before() =>
        new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, ShellCreated)
            .Process(Compositor, 1, "dwm.exe", 150, CompositorCreated)
            .Process(Own, 1, "Deguffer.exe", 100, OwnCreated)
            .Process(Host, 1, "svchost.exe", 60, HostCreated)
            .Process(TargetId, Shell, "editor.exe", 300, TargetCreated)
            .Process(Child, TargetId, "editor-helper.exe", 50, ChildCreated)
            .Service("Thing", Host)
            .Build();

    /// <summary>The machine when the watch ends, holding only what <paramref name="running"/> names.</summary>
    private static MemorySnapshot After(params int[] running)
    {
        var builder = new MemorySnapshotBuilder();

        foreach (var process in Before().Processes.Processes.Where(p => running.Contains(p.ProcessId)))
        {
            builder.Process(
                process.ProcessId,
                process.ParentProcessId,
                process.Name,
                process.PrivateWorkingSet!.Value / MemorySnapshotBuilder.MiB,
                process.CreationTime!.Value);
        }

        return builder.Build();
    }

    private static ProcessMemory Target(MemorySnapshot snapshot) =>
        snapshot.Processes.Processes.Single(p => p.ProcessId == TargetId);

    /// <summary>Windows as it answers about a machine nothing has changed: the target holds its window.</summary>
    private static FakeProcessCalls Processes(FakeProcess? target = null) =>
        new FakeProcessCalls()
            .With(target ?? new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated })
            .With(new FakeProcess { ProcessId = Compositor, CreatedAt = CompositorCreated })
            .With(new FakeProcess { ProcessId = Shell, CreatedAt = ShellCreated });

    private static FakeWindowCalls Desktop(params FakeWindow[] windows)
    {
        var calls = new FakeWindowCalls { Shell = ShellWindow };

        calls.With(new FakeWindow { Handle = ShellWindow, ProcessId = Shell });

        foreach (var window in windows)
        {
            calls.With(window);
        }

        return calls;
    }

    private static ProcessCloser Closer(
        FakeProcessCalls processes,
        FakeWindowCalls windows,
        IMemorySource memory,
        TimeProvider? time,
        ShellOwner shell) =>
        new(processes, windows, new FakeDesktopFacts(shell), memory, time ?? TimeProvider.System, Own);

    private static FakeWindow Window(nint handle = TargetWindow, IReadOnlyList<int?>? owners = null) =>
        new() { Handle = handle, ProcessId = TargetId, Owners = owners };

    private static ProcessCloser Closer(
        FakeProcessCalls processes, FakeWindowCalls windows, IMemorySource memory, TimeProvider? time = null) =>
        Closer(processes, windows, memory, time, ShellOwner.Is(Shell));

    /// <summary>
    /// A running program, asked to close, that exits while Deguffer watches.
    ///
    /// <para>It cannot have exited beforehand: §7.2.1 refuses a process that is not the one the user
    /// picked, and one that has already gone is exactly that. So the exit happens where a real one
    /// does — after the messages are posted, at a moment nothing here decides in advance.</para>
    /// </summary>
    private static async Task<CloseAttempt> ClosedWhileWatchedAsync(
        ProcessCloser closer, ProcessMemory target, FakeProcess process, ManualTimeProvider clock)
    {
        var closing = closer.CloseAsync(target);

        await clock.WhenWaitingAsync(TimeSpan.FromSeconds(5));

        process.Exited = true;
        clock.Advance(ProcessCloser.WatchCadence);

        return await closing;
    }

    /// <summary>
    /// The whole of a close that went as asked: the window was posted to, the desktop survived, the
    /// program's own child is named as expected, and the service host that went with it is listed
    /// without being claimed.
    /// </summary>
    [Fact]
    public async Task ACloseThatWentAsAskedPostsWatchesAndAccountsForEveryExit()
    {
        var before = Before();
        var windows = Desktop(Window());
        var target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated };
        var clock = new ManualTimeProvider();
        var closer = Closer(Processes(target), windows, new QueuedMemorySource(before, After(Shell, Compositor, Own)), clock);

        var attempt = await ClosedWhileWatchedAsync(closer, Target(before), target, clock);

        Assert.True(attempt.Verdict.IsAllowed, attempt.Verdict.Reason);
        Assert.Equal(TargetWindow, Assert.Single(windows.Posted));

        var report = Assert.IsType<CloseReport>(attempt.Report);

        Assert.Equal(CloseState.Closed, report.State);
        Assert.True(report.Verification.Passed);

        var outcomes = report.Verification.Checks.ToDictionary(c => c.Subject, c => c.Outcome, StringComparer.Ordinal);

        Assert.Equal(VerificationOutcome.Sent, outcomes[$"window 0x11 (AnApplicationWindow) of editor.exe (process {TargetId})"]);
        Assert.Equal(VerificationOutcome.Survived, outcomes[$"explorer.exe (process {Shell})"]);
        Assert.Equal(VerificationOutcome.Survived, outcomes[$"dwm.exe (process {Compositor})"]);
        Assert.Equal(VerificationOutcome.ExpectedExit, outcomes[$"editor-helper.exe (process {Child})"]);
        Assert.Equal(VerificationOutcome.UnclaimedExit, outcomes[$"svchost.exe (process {Host})"]);
    }

    /// <summary>
    /// §7.2.1's second decision, and the reason it exists: the identifier the user picked now belongs
    /// to a process created at another moment, so nothing is sent to it at all.
    /// </summary>
    [Fact]
    public async Task ACreationTimeThatChangedBetweenTheSelectionAndTheActionSendsNothing()
    {
        var before = Before();
        var windows = Desktop(Window());
        var closer = Closer(
            Processes(new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated + 500 }),
            windows,
            new QueuedMemorySource(before, After(Shell, Compositor, Own)));

        // A refusal returns rather than going on to watch anything, so this needs no clock. The
        // wait is what says so: a close that reached the watch would hold here instead.
        var attempt = await closer.CloseAsync(Target(before)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(attempt.Verdict.IsAllowed);
        Assert.Null(attempt.Report);
        Assert.Empty(windows.Posted);
        Assert.Contains("has gone", attempt.Verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A window handle is recycled as an identifier is, so the owner is asked again immediately
    /// before each message. One that has passed to another program receives nothing, and the report
    /// says so rather than counting it as asked.
    /// </summary>
    [Fact]
    public async Task AWindowThatChangedHandsBetweenTheSurveyAndThePostReceivesNothing()
    {
        var before = Before();
        var windows = Desktop(Window(), Window(SecondWindow, owners: [TargetId, 7777]));
        var target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated };
        var clock = new ManualTimeProvider();
        var closer = Closer(Processes(target), windows, new QueuedMemorySource(before, After(Shell, Compositor, Own)), clock);

        var attempt = await ClosedWhileWatchedAsync(closer, Target(before), target, clock);

        Assert.Equal(TargetWindow, Assert.Single(windows.Posted));

        var report = Assert.IsType<CloseReport>(attempt.Report);

        Assert.Equal(1, report.Windows);
        Assert.Equal(1, report.Moved);
        Assert.Contains("no longer reported as this program's", report.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where every window has changed hands, there is nothing to send, nothing to watch and nothing
    /// to report: the action is refused rather than reported as a close that did nothing.
    /// </summary>
    [Fact]
    public async Task AProgramWhoseEveryWindowChangedHandsIsRefusedRatherThanReported()
    {
        var before = Before();
        var windows = Desktop(Window(owners: [TargetId, 7777]));
        var closer = Closer(
            Processes(),
            windows,
            new QueuedMemorySource(before, After(Shell, Compositor, Own)));

        // Bounded for the reason the creation-time test is: a close that reached the watch would
        // hold here for ever, and a hang is not a failure anybody can read.
        var attempt = await closer.CloseAsync(Target(before)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(windows.Posted);
        Assert.Null(attempt.Report);
        Assert.False(attempt.Verdict.IsAllowed);
        Assert.Contains("belong to another program", attempt.Verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The alarm §5.6 exists to raise, through the whole action: the shell is named from the shell
    /// window rather than from an image name, and a run that lost it fails and says which process it
    /// was.
    /// </summary>
    [Fact]
    public async Task AShellThatDidNotSurviveFailsTheRunAndNamesItself()
    {
        var before = Before();
        var target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated };
        var clock = new ManualTimeProvider();
        var closer = Closer(Processes(target), Desktop(Window()), new QueuedMemorySource(before, After(Compositor, Own)), clock);

        var report = Assert.IsType<CloseReport>(
            (await ClosedWhileWatchedAsync(closer, Target(before), target, clock)).Report);

        Assert.False(report.Verification.Passed);
        Assert.Equal($"explorer.exe (process {Shell})", Assert.Single(report.Verification.Failures).Subject);
        Assert.Contains("did not pass", report.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// The watch has no deadline: it keeps asking the handle it holds, and answers the moment the
    /// process exits rather than after any period.
    /// </summary>
    [Fact]
    public async Task TheWatchWaitsForTheProgramRatherThanForAnyLengthOfTime()
    {
        var before = Before();
        var target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated, Exited = false };
        var clock = new ManualTimeProvider();
        var closer = Closer(
            Processes(target), Desktop(Window()), new QueuedMemorySource(before, After(Shell, Compositor, Own)), clock);

        var closing = closer.CloseAsync(Target(before));

        await clock.WhenWaitingAsync(TimeSpan.FromSeconds(5));
        Assert.False(closing.IsCompleted);

        // The program answers its own save prompt, some unknowable time later.
        target.Exited = true;
        clock.Advance(ProcessCloser.WatchCadence);

        var report = Assert.IsType<CloseReport>((await closing).Report);

        Assert.Equal(CloseState.Closed, report.State);
        Assert.NotNull(report.After);
    }

    /// <summary>
    /// §7.2.1: a program still running when the watch ends is reported, not escalated, and it shows
    /// no after figure at all. §5.6 still runs, because the watch ending is when the user is owed the
    /// evidence.
    /// </summary>
    [Fact]
    public async Task AProgramStillRunningWhenTheUserStopsWatchingIsReportedWithNoAfterFigure()
    {
        var before = Before();
        using var stop = new CancellationTokenSource();
        var closer = Closer(
            Processes(),
            Desktop(Window()),
            new QueuedMemorySource(before, After(Shell, Compositor, Own, TargetId, Child, Host)));

        // The user dismisses the result the moment it appears, which is one of §7.2.1's three ends.
        var attempt = await closer.CloseAsync(Target(before), new WhenPosted(_ => stop.Cancel()), stop.Token);

        var report = Assert.IsType<CloseReport>(attempt.Report);

        Assert.Equal(CloseState.StillRunning, report.State);
        Assert.Null(report.After);
        Assert.Contains("still running", report.Statement, StringComparison.Ordinal);
        Assert.NotEmpty(report.Verification.Checks);
    }

    /// <summary>
    /// §7.2: nothing is asked of Windows for a row nobody selected. A close opens the target it acts
    /// on, the shell whose identity §5.6 has to confirm, and the compositor whose session decides
    /// whether it is this session's. No other process is opened, however many the machine holds.
    /// </summary>
    [Fact]
    public async Task OnlyTheTargetAndTheDesktopAreOpened()
    {
        var before = Before();
        var target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated };
        var processes = Processes(target);
        var clock = new ManualTimeProvider();
        var closer = Closer(processes, Desktop(Window()), new QueuedMemorySource(before, After(Shell, Compositor, Own)), clock);

        await ClosedWhileWatchedAsync(closer, Target(before), target, clock);

        Assert.Equal([TargetId, Shell, Compositor], processes.Opened);
    }

    /// <summary>
    /// §7.2.1: "There is no deadline. A save prompt waits for a person, so a timer would report
    /// 'still running' about a program doing exactly what it was asked."
    ///
    /// <para>Proven by leaving the program to think for two days of the watch's own clock. A deadline
    /// of any length, and any limit on how many times the handle is asked, would have given up long
    /// before the program answered.</para>
    /// </summary>
    [Fact]
    public async Task TheWatchWaitsForAsLongAsTheProgramTakes()
    {
        var before = Before();
        var target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated };
        var clock = new ManualTimeProvider();
        var closer = Closer(
            Processes(target), Desktop(Window()), new QueuedMemorySource(before, After(Shell, Compositor, Own)), clock);

        var closing = closer.CloseAsync(Target(before));

        for (var hour = 0; hour < 48; hour++)
        {
            await clock.WhenWaitingAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.False(closing.IsCompleted);

        await clock.WhenWaitingAsync(TimeSpan.FromSeconds(5));
        target.Exited = true;
        clock.Advance(ProcessCloser.WatchCadence);

        Assert.Equal(CloseState.Closed, Assert.IsType<CloseReport>((await closing).Report).State);
    }

    /// <summary>
    /// §7.2.1: the handle is held until the action finishes, and that "removes the reuse race rather
    /// than narrowing it". Every window line in the evidence rests on it, so the moment that matters
    /// is the one where the messages have gone out and the watch has not yet ended.
    /// </summary>
    [Fact]
    public async Task TheProcessIsHeldOpenAcrossThePostAndTheWatch()
    {
        var before = Before();
        var target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated };
        var clock = new ManualTimeProvider();
        var closer = Closer(
            Processes(target), Desktop(Window()), new QueuedMemorySource(before, After(Shell, Compositor, Own)), clock);

        var held = false;
        var closing = closer.CloseAsync(Target(before), new WhenPosted(_ => held = !target.Disposed));

        await clock.WhenWaitingAsync(TimeSpan.FromSeconds(5));
        target.Exited = true;
        clock.Advance(ProcessCloser.WatchCadence);
        await closing;

        Assert.True(held, "The process was closed before its windows were posted to.");
        Assert.True(target.Disposed, "The process was left open after the watch ended.");
    }

    /// <summary>
    /// §7.2.1 decides Deguffer's own tree and every §5.6 assertion from the read taken as the action
    /// begins. A read that cannot tell one process from another can answer neither, so the close is
    /// refused rather than sent on a machine nobody could account for.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReadThatCannotTellProcessesApartRefusesRatherThanActs(bool figuresOff)
    {
        var before = Before();
        var degraded = figuresOff
            ? before with { Processes = before.Processes with { Figures = ProcessFigures.CreationTimeDisagrees } }
            : before with { Processes = before.Processes with { Complete = false } };

        var processes = Processes();
        var windows = Desktop(Window());
        var closer = Closer(processes, windows, new QueuedMemorySource(degraded));

        var attempt = await closer.CloseAsync(Target(before)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(attempt.Verdict.IsAllowed);
        Assert.Null(attempt.Report);
        Assert.Empty(windows.Posted);
        Assert.Empty(processes.Opened);
        Assert.Contains("tell them apart", attempt.Verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A close cannot be recalled, so the machine refusing to describe itself afterwards must not
    /// take the report with it. What was posted is still the exact record §7.2.1 asks for, and what
    /// could not be checked is recorded as unestablished rather than passed.
    /// </summary>
    [Fact]
    public async Task AMachineThatWillNotAnswerWhenTheWatchEndsStillGetsAReport()
    {
        var before = Before();
        var target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated };
        var clock = new ManualTimeProvider();
        var windows = Desktop(Window());
        var memory = new QueuedMemorySource(before) { RefusesFrom = 2 };
        var closer = Closer(Processes(target), windows, memory, clock);

        var closing = closer.CloseAsync(Target(before));

        await clock.WhenWaitingAsync(TimeSpan.FromSeconds(5));
        target.Exited = true;
        clock.Advance(ProcessCloser.WatchCadence);

        var report = Assert.IsType<CloseReport>((await closing).Report);

        Assert.Equal(CloseState.Closed, report.State);
        Assert.Null(report.After);
        Assert.Equal(TargetWindow, Assert.Single(windows.Posted));
        Assert.Contains(
            report.Verification.Checks,
            c => c.Outcome == VerificationOutcome.Sent && c.Subject.Contains("0x11", StringComparison.Ordinal));
        Assert.All(
            report.Verification.Failures,
            c => Assert.Contains("NOT ESTABLISHED", c.Detail, StringComparison.Ordinal));
        Assert.NotEmpty(report.Verification.Failures);
    }

    /// <summary>
    /// A window Windows will not attribute is not a window Deguffer may post to. It is skipped for
    /// the same reason a reassigned one is: the only sound answer is the one asked at the moment of
    /// posting, and there is none.
    /// </summary>
    [Fact]
    public async Task AWindowWindowsWillNotAttributeReceivesNothing()
    {
        var before = Before();
        var target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated };
        var clock = new ManualTimeProvider();
        var windows = Desktop(Window(), Window(SecondWindow, owners: [TargetId, null]));
        var closer = Closer(
            Processes(target), windows, new QueuedMemorySource(before, After(Shell, Compositor, Own)), clock);

        var attempt = await ClosedWhileWatchedAsync(closer, Target(before), target, clock);

        Assert.Equal(TargetWindow, Assert.Single(windows.Posted));
        Assert.Equal(1, Assert.IsType<CloseReport>(attempt.Report).Moved);
    }

    /// <summary>
    /// §7.2.1 decides before posting and never by what the post reports, because Microsoft's own
    /// sources disagree about what a blocked post says. What Windows answered is recorded all the
    /// same, since the evidence is a record of what happened rather than of what was intended.
    /// </summary>
    [Fact]
    public async Task APostWindowsWillNotTakeIsRecordedAsOne()
    {
        var before = Before();
        var target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated };
        var clock = new ManualTimeProvider();
        var windows = Desktop(Window());
        windows.PostSucceeds = false;

        var closer = Closer(
            Processes(target), windows, new QueuedMemorySource(before, After(Shell, Compositor, Own)), clock);

        var attempt = await ClosedWhileWatchedAsync(closer, Target(before), target, clock);

        var posted = Assert.Single(
            Assert.IsType<CloseReport>(attempt.Report).Verification.Checks,
            c => c.Subject.Contains("0x11", StringComparison.Ordinal));

        Assert.Equal(VerificationOutcome.Sent, posted.Outcome);
        Assert.Contains("did not take it", posted.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.2.1: a close cannot be called off. The token ends the watch and nothing else, so a user
    /// who stops watching at the instant the message goes out is still owed the evidence, and still
    /// gets it.
    /// </summary>
    [Fact]
    public async Task StoppingTheWatchAsTheMessageGoesOutStillGathersTheEvidence()
    {
        var before = Before();
        using var stop = new CancellationTokenSource();
        var windows = Desktop(Window());
        windows.WhenPosted = stop.Cancel;

        var closer = Closer(
            Processes(),
            windows,
            new QueuedMemorySource(before, After(Shell, Compositor, Own, TargetId, Child, Host)));

        var attempt = await closer
            .CloseAsync(Target(before), watching: null, stop.Token)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var report = Assert.IsType<CloseReport>(attempt.Report);

        Assert.Equal(CloseState.StillRunning, report.State);
        Assert.Contains(
            report.Verification.Checks,
            c => c.Subject.StartsWith("explorer.exe", StringComparison.Ordinal)
                && c.Outcome == VerificationOutcome.Survived);
    }

    /// <summary>
    /// Runs a test's own check at the one moment nothing else can reach: the messages are posted, the
    /// handle is still held, and the watch has not begun.
    /// </summary>
    private sealed class WhenPosted(Action<CloseReport> check) : IProgress<CloseReport>
    {
        public void Report(CloseReport value) => check(value);
    }
}
