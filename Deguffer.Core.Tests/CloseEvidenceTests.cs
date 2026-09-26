using Deguffer.Core.Execution;
using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.6 for an action whose subject exits on its own (§7.2.1).
///
/// <para>Every process here is invented, and both snapshots are written by the test: what a close
/// establishes is a comparison of two reads, and a comparison proven against this machine's own
/// process table would prove whatever that machine did while the test ran.</para>
/// </summary>
public sealed class CloseEvidenceTests
{
    private const int Shell = 100;
    private const int Compositor = 120;
    private const int Target = 400;
    private const int Child = 410;
    private const int Grandchild = 420;
    private const int ServiceHost = 500;
    private const int Stranger = 600;

    /// <summary>
    /// The machine as the first message was posted: the desktop, the program with two processes
    /// under it, a service host, and one program that has nothing to do with any of it.
    /// </summary>
    private static MemorySnapshot Before() =>
        new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, created: 1)
            .Process(Compositor, 1, "dwm.exe", 150, created: 1)
            .Process(ServiceHost, 1, "svchost.exe", 60, created: 2)
            .Process(Stranger, 1, "other.exe", 40, created: 3)
            .Process(Target, Shell, "editor.exe", 300, created: 5)
            .Process(Child, Target, "editor-helper.exe", 50, created: 6)
            .Process(Grandchild, Child, "editor-render.exe", 70, created: 7)
            .Service("Thing", ServiceHost)
            .Build();

    /// <summary>The same machine with only <paramref name="running"/> left, and nothing new started.</summary>
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

        return builder.Service("Thing", ServiceHost).Build();
    }

    private static ProcessMemory Process(MemorySnapshot snapshot, int id) =>
        snapshot.Processes.Processes.Single(p => p.ProcessId == id);

    private static DesktopSet Desktop(MemorySnapshot before) =>
        new([Process(before, Shell), Process(before, Compositor)], []);

    private static IReadOnlyList<VerificationCheck> Evidence(
        MemorySnapshot before, MemorySnapshot? after, DesktopSet? desktop = null) =>
        CloseEvidence.Of(
            ProcessTree.Of(before),
            after is null ? null : ProcessTree.Of(after),
            Process(before, Target),
            desktop ?? Desktop(before));

    private static VerificationCheck For(IReadOnlyList<VerificationCheck> checks, string name) =>
        checks.Single(c => c.Subject.StartsWith(name, StringComparison.Ordinal));

    /// <summary>
    /// The whole of what a close asserts, on the ordinary run: the desktop is still there, the
    /// program's own two processes went with it, and the service host that exited beside them is
    /// named without being claimed.
    /// </summary>
    [Fact]
    public void TheDesktopSurvivesTheChildrenAreExpectedAndTheRestIsNotClaimed()
    {
        var before = Before();

        var checks = Evidence(before, After(Shell, Compositor, Stranger));

        Assert.Equal(VerificationOutcome.Survived, For(checks, "explorer.exe").Outcome);
        Assert.Equal(VerificationOutcome.Survived, For(checks, "dwm.exe").Outcome);
        Assert.Equal(VerificationOutcome.ExpectedExit, For(checks, "editor-helper.exe").Outcome);
        Assert.Equal(VerificationOutcome.ExpectedExit, For(checks, "editor-render.exe").Outcome);
        Assert.Equal(VerificationOutcome.UnclaimedExit, For(checks, "svchost.exe").Outcome);

        // The target's own exit is the action's result rather than a §5.6 assertion, and the report
        // states it. A check about it here would read as one more thing that had to survive.
        Assert.DoesNotContain(checks, c => c.Subject.StartsWith("editor.exe", StringComparison.Ordinal));

        Assert.True(new VerificationResult { Checks = checks }.Passed);
    }

    /// <summary>
    /// The alarm §5.6 exists to raise. Asking a program to close cannot end the shell, so a shell
    /// that is gone means something reached further than it was meant to, and the row names which
    /// process it was.
    /// </summary>
    [Fact]
    public void AShellThatIsGoneFailsTheRunAndNamesItself()
    {
        var before = Before();

        var checks = Evidence(before, After(Compositor, Stranger));

        var failure = Assert.Single(new VerificationResult { Checks = checks }.Failures);

        Assert.Equal($"explorer.exe (process {Shell})", failure.Subject);
        Assert.Contains("MISSING", failure.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Identity is the identifier with the creation time, because Windows reuses identifiers. A new
    /// process holding the shell's old number is not the shell.
    /// </summary>
    [Fact]
    public void AnIdentifierAnotherProcessNowHoldsIsNotTheProcessThatHadIt()
    {
        var before = Before();

        var after = new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, created: 90)
            .Process(Compositor, 1, "dwm.exe", 150, created: 1)
            .Build();

        var checks = Evidence(before, after);

        Assert.Equal(VerificationOutcome.Failed, For(checks, "explorer.exe").Outcome);
    }

    /// <summary>
    /// A read that cannot say what is still running establishes nothing, and filing that as evidence
    /// is the one thing that undoes §5.6. Both ways a read falls short answer the same way: the
    /// processes that had to survive are recorded as unestablished, and no exit is listed at all.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReadThatCannotSayWhatIsRunningEstablishesNothing(bool figuresOff)
    {
        var before = Before();
        var after = After(Shell, Compositor, Stranger);

        after = figuresOff
            ? after with { Processes = after.Processes with { Figures = ProcessFigures.CreationTimeDisagrees } }
            : after with { Processes = after.Processes with { Complete = false } };

        var checks = Evidence(before, after);

        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(VerificationOutcome.Failed, c.Outcome));
        Assert.All(checks, c => Assert.Contains("NOT ESTABLISHED", c.Detail, StringComparison.Ordinal));
    }

    /// <summary>
    /// A process that had to survive and could not be named is the assertion §7.2.1 calls exact,
    /// missing. Recording it as a failure is what keeps a close that verified nothing from reading
    /// as a clean run.
    /// </summary>
    [Fact]
    public void SomethingThatHadToSurviveAndCouldNotBeNamedFailsTheRun()
    {
        var before = Before();
        var desktop = new DesktopSet([Process(before, Compositor)], ["the process owning the shell window"]);

        var checks = Evidence(before, After(Shell, Compositor, Stranger), desktop);
        var result = new VerificationResult { Checks = checks };

        Assert.False(result.Passed);

        var failure = Assert.Single(result.Failures);

        Assert.Equal("the process owning the shell window", failure.Subject);
        Assert.Contains("NOT ESTABLISHED", failure.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A close cannot be recalled, so a machine that will not describe itself when the watch ends
    /// leaves a report to write. It says what it could not check, which is everything.
    /// </summary>
    [Fact]
    public void AMachineThatCouldNotBeReadWhenTheWatchEndedEstablishesNothing()
    {
        var before = Before();

        var checks = Evidence(before, after: null);

        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(VerificationOutcome.Failed, c.Outcome));
        Assert.All(checks, c => Assert.Contains("NOT ESTABLISHED", c.Detail, StringComparison.Ordinal));
    }

    /// <summary>
    /// The case §7.2.1 wrote item 4 for: a shared host exits when its last service stops, so closing
    /// a program that was a service's only client can end a host with nothing sent to it. The host
    /// recorded that program as its parent, and it is still not one of its expected exits — §7.2 puts
    /// no process under a service host, and a host is every service inside it.
    /// </summary>
    [Fact]
    public void AServiceHostThatWentWithTheProgramIsListedRatherThanExpected()
    {
        var before = new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, created: 1)
            .Process(Compositor, 1, "dwm.exe", 150, created: 1)
            .Process(Target, Shell, "editor.exe", 300, created: 5)
            .Process(ServiceHost, Target, "svchost.exe", 60, created: 6)
            .Service("Thing", ServiceHost)
            .Build();

        var after = new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, created: 1)
            .Process(Compositor, 1, "dwm.exe", 150, created: 1)
            .Build();

        var checks = Evidence(before, after);

        Assert.Equal(VerificationOutcome.UnclaimedExit, For(checks, "svchost.exe").Outcome);
        Assert.True(new VerificationResult { Checks = checks }.Passed);
    }
}
