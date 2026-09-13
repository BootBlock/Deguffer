using Deguffer.Core.Memory;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.2.1 decides what it refuses from these facts, so each has to carry three answers rather than
/// two, and a process that is not the one the user picked has to carry none at all. These drive the
/// seam over fakes: this machine cannot be made to hold a process that exits between two calls, a
/// token it will not open, or an identifier that has passed to a later process.
/// </summary>
public sealed class ProcessFactSourceTests
{
    private const int Picked = 4_200;

    [Fact]
    public void EveryFactOfThePickedProcessIsRead()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess
        {
            ProcessId = Picked,
            Session = FakeProcessCalls.OwnSession,
            User = FakeProcessCalls.OwnUser,
            Integrity = FakeProcessCalls.Medium,
            Critical = false,
            Packaging = PackageIdentity.NotPackaged,
        }));

        Assert.Equal(Answer.Yes, facts.Present);
        Assert.Equal(Answer.Yes, facts.InOwnSession);
        Assert.Equal(Answer.Yes, facts.AsOwnAccount);
        Assert.Equal(Answer.No, facts.AboveOwnIntegrity);
        Assert.Equal(Answer.No, facts.Critical);
        Assert.Equal(PackageAnswer.NotPackaged, facts.Package);
        Assert.Equal(Answer.No, facts.OwnsConsoleWindow);

        // A process with no window that qualifies answers an empty set, never a null.
        Assert.Empty(facts.Windows!);
    }

    /// <summary>
    /// The identifier is held by a process created after the one the user picked, so that one has gone
    /// and the number belongs to a stranger. Nothing about the stranger is read.
    /// </summary>
    [Fact]
    public void AnIdentifierThatNowBelongsToALaterProcessReadsAsGoneAndNothingElseIsRead()
    {
        var process = new FakeProcess { ProcessId = Picked, CreatedAt = FakeProcess.Created + 1 };

        var facts = Read(new FakeProcessCalls().With(process));

        Assert.Equal(ProcessFacts.NothingRead(Answer.No), facts);
        Assert.Equal(0, process.FactsRead);
    }

    [Fact]
    public void AProcessThatHasExitedWhileStillOpenReadsAsGone()
    {
        var process = new FakeProcess { ProcessId = Picked, Exited = true };

        Assert.Equal(ProcessFacts.NothingRead(Answer.No), Read(new FakeProcessCalls().With(process)));
        Assert.Equal(0, process.FactsRead);
    }

    [Fact]
    public void AnIdentifierNoProcessHoldsReadsAsGone() =>
        Assert.Equal(ProcessFacts.NothingRead(Answer.No), Read(new FakeProcessCalls()));

    /// <summary>
    /// An open Windows refused is not evidence that the process has gone, and §7.2.1 refuses on an
    /// unreadable fact exactly as it refuses on a bad one.
    /// </summary>
    [Fact]
    public void AProcessWindowsWillNotOpenLeavesEveryFactUnreadable()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess
        {
            ProcessId = Picked,
            OpenRefused = true,
        }));

        Assert.Equal(ProcessFacts.NothingRead(Answer.Unreadable), facts);
    }

    /// <summary>
    /// No successor predates the process it replaced, so a holder created earlier than the recorded
    /// time says the record is not this identifier's, and nothing follows from it.
    /// </summary>
    [Fact]
    public void ACreationTimeEarlierThanTheOneRecordedSaysNothing()
    {
        var process = new FakeProcess { ProcessId = Picked, CreatedAt = FakeProcess.Created - 1 };

        Assert.Equal(ProcessFacts.NothingRead(Answer.Unreadable), Read(new FakeProcessCalls().With(process)));
        Assert.Equal(0, process.FactsRead);
    }

    [Fact]
    public void ACreationTimeThatWillNotBeReadSaysNothing()
    {
        var process = new FakeProcess { ProcessId = Picked, CreatedAt = null };

        Assert.Equal(ProcessFacts.NothingRead(Answer.Unreadable), Read(new FakeProcessCalls().With(process)));
        Assert.Equal(0, process.FactsRead);
    }

    [Fact]
    public void AnExitThatWillNotBeReadSaysNothing()
    {
        var process = new FakeProcess { ProcessId = Picked, Exited = null };

        Assert.Equal(ProcessFacts.NothingRead(Answer.Unreadable), Read(new FakeProcessCalls().With(process)));
        Assert.Equal(0, process.FactsRead);
    }

    [Fact]
    public void ASessionWindowsWillNotReadIsUnreadableRatherThanAnotherSession()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess { ProcessId = Picked, Session = null }));

        Assert.Equal(Answer.Unreadable, facts.InOwnSession);
        Assert.Equal(Answer.Yes, facts.AsOwnAccount);
    }

    [Fact]
    public void AnAccountWindowsWillNotReadIsUnreadableRatherThanAnotherAccount()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess { ProcessId = Picked, User = null }));

        Assert.Equal(Answer.Unreadable, facts.AsOwnAccount);
        Assert.Equal(Answer.Yes, facts.InOwnSession);
    }

    [Fact]
    public void AnIntegrityLevelWindowsWillNotReadIsUnreadableRatherThanNotAbove()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess { ProcessId = Picked, Integrity = null }));

        Assert.Equal(Answer.Unreadable, facts.AboveOwnIntegrity);
    }

    /// <summary>
    /// §7.2.1 refuses a process whose criticality will not be read exactly as it refuses a critical
    /// one, so the two must not arrive as the same answer.
    /// </summary>
    [Fact]
    public void ACriticalityWindowsWillNotReadIsUnreadableRatherThanNotCritical()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess { ProcessId = Picked, Critical = null }));

        Assert.Equal(Answer.Unreadable, facts.Critical);
    }

    [Fact]
    public void ACriticalProcessIsAnswered()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess { ProcessId = Picked, Critical = true }));

        Assert.Equal(Answer.Yes, facts.Critical);
    }

    [Fact]
    public void AnotherSessionAndAnotherAccountAreAnsweredSeparately()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess
        {
            ProcessId = Picked,
            Session = FakeProcessCalls.OtherSession,
            User = FakeProcessCalls.OtherUser,
        }));

        Assert.Equal(Answer.No, facts.InOwnSession);
        Assert.Equal(Answer.No, facts.AsOwnAccount);
    }

    [Fact]
    public void AProcessAboveDegufferIsAnswered()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess
        {
            ProcessId = Picked,
            Integrity = FakeProcessCalls.High,
        }));

        Assert.Equal(Answer.Yes, facts.AboveOwnIntegrity);
    }

    [Fact]
    public void APackagedProcessWindowsHasFrozenIsSuspended()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess
        {
            ProcessId = Picked,
            Packaging = PackageIdentity.Packaged,
            Frozen = true,
        }));

        Assert.Equal(PackageAnswer.Suspended, facts.Package);
    }

    [Fact]
    public void APackagedProcessThatIsNotFrozenIsRunning()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess
        {
            ProcessId = Picked,
            Packaging = PackageIdentity.Packaged,
            Frozen = false,
        }));

        Assert.Equal(PackageAnswer.Running, facts.Package);
    }

    /// <summary>
    /// A packaged process whose state will not be read is its own answer: §7.2.1 refuses it, and it is
    /// not the same thing as a process with no package identity.
    /// </summary>
    [Fact]
    public void APackagedProcessWhoseStateWillNotBeReadIsItsOwnAnswer()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess
        {
            ProcessId = Picked,
            Packaging = PackageIdentity.Packaged,
            Frozen = null,
        }));

        Assert.Equal(PackageAnswer.StateUnreadable, facts.Package);
    }

    [Fact]
    public void WhetherAProcessIsPackagedMayItselfBeUnreadable()
    {
        var facts = Read(new FakeProcessCalls().With(new FakeProcess
        {
            ProcessId = Picked,
            Packaging = PackageIdentity.Unreadable,
        }));

        Assert.Equal(PackageAnswer.Unreadable, facts.Package);
    }

    /// <summary>
    /// §7.2.1: nothing is asked of Windows for a row nobody selected. A table of five processes, one
    /// row picked, one process opened.
    /// </summary>
    [Fact]
    public void AskingAboutOneRowOpensOneProcess()
    {
        var calls = new FakeProcessCalls();
        var others = Enumerable.Range(1, 5).Select(offset => new FakeProcess { ProcessId = Picked + offset }).ToList();

        calls.With(new FakeProcess { ProcessId = Picked });
        others.ForEach(process => calls.With(process));

        Read(calls);

        Assert.Equal([Picked], calls.Opened);
        Assert.All(others, process => Assert.Equal(0, process.FactsRead));
    }

    [Fact]
    public void TheHandleIsClosedWhenEveryFactHasBeenRead()
    {
        var process = new FakeProcess { ProcessId = Picked };

        Read(new FakeProcessCalls().With(process));

        Assert.True(process.Disposed);
    }

    [Fact]
    public void TheHandleIsClosedWhenTheProcessTurnsOutToHaveGone()
    {
        var process = new FakeProcess { ProcessId = Picked, Exited = true };

        Read(new FakeProcessCalls().With(process));

        Assert.True(process.Disposed);
    }

    /// <summary>
    /// What Deguffer's own process is cannot change while it runs, so it is read once however many
    /// rows the user picks (G5).
    /// </summary>
    [Fact]
    public void DegufferReadsItsOwnFactsOnce()
    {
        var calls = new FakeProcessCalls().With(new FakeProcess { ProcessId = Picked });
        var source = new ProcessFactSource(calls, new FakeWindowCalls());

        source.Read(Picked, FakeProcess.Created, CancellationToken.None);
        source.Read(Picked, FakeProcess.Created, CancellationToken.None);

        Assert.Equal(1, calls.OwnReads);
    }

    /// <summary>
    /// A partial reading of Deguffer's own process is not kept: keeping one would refuse every process
    /// for the rest of the run over a question that was asked once and failed.
    /// </summary>
    [Fact]
    public void OwnFactsThatCouldNotBeReadWholeAreAskedAgain()
    {
        var calls = new FakeProcessCalls { OwnFacts = new OwnProcess(FakeProcessCalls.OwnSession, User: null, FakeProcessCalls.Medium) };
        calls.With(new FakeProcess { ProcessId = Picked });
        var source = new ProcessFactSource(calls, new FakeWindowCalls());

        Assert.Equal(Answer.Unreadable, source.Read(Picked, FakeProcess.Created, CancellationToken.None).AsOwnAccount);

        calls.OwnFacts = new OwnProcess(FakeProcessCalls.OwnSession, FakeProcessCalls.OwnUser, FakeProcessCalls.Medium);

        Assert.Equal(Answer.Yes, source.Read(Picked, FakeProcess.Created, CancellationToken.None).AsOwnAccount);
        Assert.Equal(2, calls.OwnReads);
    }

    [Fact]
    public void ACancelledReadOpensNothing()
    {
        var calls = new FakeProcessCalls().With(new FakeProcess { ProcessId = Picked });
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new ProcessFactSource(calls, new FakeWindowCalls()).Read(Picked, FakeProcess.Created, cancel.Token));

        Assert.Empty(calls.Opened);
    }

    /// <summary>The idle process at 0 answers as a free identifier does, so it is refused as an argument.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnIdentifierBelowOneIsRefused(int processId) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProcessFactSource(new FakeProcessCalls(), new FakeWindowCalls())
                .Read(processId, FakeProcess.Created, CancellationToken.None));

    /// <summary>The window survey is of the picked process, and its windows arrive with their classes.</summary>
    [Fact]
    public void TheWindowsOfThePickedProcessArriveWithTheirClasses()
    {
        var windows = new FakeWindowCalls()
            .With(new FakeWindow { Handle = 11, ProcessId = Picked, ClassName = "AWindowClass" })
            .With(new FakeWindow { Handle = 12, ProcessId = Picked + 1 });

        var facts = Read(new FakeProcessCalls().With(new FakeProcess { ProcessId = Picked }), windows);

        Assert.Equal([new ProcessWindow(11, "AWindowClass")], facts.Windows);
        Assert.Equal(Answer.No, facts.OwnsConsoleWindow);
    }

    private static ProcessFacts Read(FakeProcessCalls processes, FakeWindowCalls? windows = null) =>
        new ProcessFactSource(processes, windows ?? new FakeWindowCalls())
            .Read(Picked, FakeProcess.Created, CancellationToken.None);
}
