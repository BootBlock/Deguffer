using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.2.1's refusal table, row by row and in its order.
///
/// <para>Every row is proven with the reason that row states, because §7.2.1 makes a refusal a
/// sentence on the row rather than a disabled button: a user who picked a process out of a picture
/// of memory and found nothing to press would learn nothing about which row of that table
/// applied.</para>
///
/// <para><see cref="MemoryActionPolicy.Decide"/> takes the facts rather than reading them, so these
/// run against facts a test wrote. Nothing here asks this machine anything, and nothing here names a
/// real program, account or machine.</para>
/// </summary>
public sealed class MemoryActionPolicyTests
{
    private const int Own = 900;
    private const int Shell = 100;
    private const int TargetId = 4321;

    private static readonly ProcessWindow Window = new(0x0004_0122, "Editor.Window");

    /// <summary>A process everything allows: this account, this session, no console, one window.</summary>
    private static ProcessFacts Allowed() => new(
        Answer.Yes,
        InOwnSession: Answer.Yes,
        AsOwnAccount: Answer.Yes,
        AboveOwnIntegrity: Answer.No,
        Critical: Answer.No,
        PackageAnswer.NotPackaged,
        OwnsConsoleWindow: Answer.No,
        Windows: [Window]);

    /// <summary>The machine the user picked from: the shell, Deguffer, a service host, and the target.</summary>
    private static MemorySnapshot Snapshot() =>
        new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, created: 1)
            .Process(Own, 1, "Deguffer.exe", 100, created: 2)
            .Process(500, 1, "svchost.exe", 60, created: 3)
            .Process(TargetId, Shell, "editor.exe", 300, created: 5)
            .Service("Thing", 500)
            .Build();

    private static ProcessMemory Process(MemorySnapshot snapshot, int id) =>
        snapshot.Processes.Processes.Single(p => p.ProcessId == id);

    private static MemoryActionPolicy Policy(ProcessFacts? facts = null, ShellOwner? shell = null) =>
        new(new FakeProcessFactSource(facts ?? Allowed()), new FakeDesktopFacts(shell ?? ShellOwner.Is(Shell)), Own);

    private static MemoryVerdict Decide(
        ProcessFacts facts, int target = TargetId, MemorySnapshot? snapshot = null, ShellOwner? shell = null)
    {
        var machine = snapshot ?? Snapshot();

        return Policy().Decide(machine, Process(machine, target), facts, shell ?? ShellOwner.Is(Shell));
    }

    [Fact]
    public void AProgramInThisSessionWithAWindowOfItsOwnMayBeAsked()
    {
        var verdict = Decide(Allowed());

        Assert.True(verdict.IsAllowed);
        Assert.Equal(Window, Assert.Single(verdict.Windows));
        Assert.DoesNotContain("safe", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The identity check §7.2.1 puts before the table: an identifier is not an identity, so a
    /// creation time that no longer matches means the process the user picked has gone.
    /// </summary>
    [Theory]
    [InlineData(Answer.No, "has gone")]
    [InlineData(Answer.Unreadable, "would not say")]
    public void AProcessThatIsNoLongerTheOnePickedIsRefused(Answer present, string says)
    {
        var verdict = Decide(Allowed() with { Present = present });

        Assert.False(verdict.IsAllowed);
        Assert.Contains(says, verdict.Reason, StringComparison.Ordinal);
        Assert.Empty(verdict.Windows);
    }

    /// <summary>
    /// Every row of the table, with the reason that row states. The unreadable answer refuses beside
    /// the answer that came back wrong, because §7.2.1 refuses on the third value exactly as it
    /// refuses on the first.
    /// </summary>
    [Theory]
    // Another session, and a session Windows would not name.
    [InlineData(nameof(ProcessFacts.InOwnSession), Answer.No, "another Windows session")]
    [InlineData(nameof(ProcessFacts.InOwnSession), Answer.Unreadable, "which session")]

    // Another account, and an account Windows would not name.
    [InlineData(nameof(ProcessFacts.AsOwnAccount), Answer.No, "another account")]
    [InlineData(nameof(ProcessFacts.AsOwnAccount), Answer.Unreadable, "which account")]

    // A critical process, and one whose criticality will not be read.
    [InlineData(nameof(ProcessFacts.Critical), Answer.Yes, "critical")]
    [InlineData(nameof(ProcessFacts.Critical), Answer.Unreadable, "whether this process is critical")]

    // A process a posted message would not reach, and one whose level will not be read.
    [InlineData(nameof(ProcessFacts.AboveOwnIntegrity), Answer.Yes, "higher integrity level")]
    [InlineData(nameof(ProcessFacts.AboveOwnIntegrity), Answer.Unreadable, "what integrity level")]

    // A console, and a window that would not say whether it is one.
    [InlineData(nameof(ProcessFacts.OwnsConsoleWindow), Answer.Yes, "console window")]
    [InlineData(nameof(ProcessFacts.OwnsConsoleWindow), Answer.Unreadable, "owns a console")]
    public void EachAnswerThatRefusesSaysWhy(string fact, Answer answer, string says)
    {
        var facts = fact switch
        {
            nameof(ProcessFacts.InOwnSession) => Allowed() with { InOwnSession = answer },
            nameof(ProcessFacts.AsOwnAccount) => Allowed() with { AsOwnAccount = answer },
            nameof(ProcessFacts.Critical) => Allowed() with { Critical = answer },
            nameof(ProcessFacts.AboveOwnIntegrity) => Allowed() with { AboveOwnIntegrity = answer },
            _ => Allowed() with { OwnsConsoleWindow = answer },
        };

        var verdict = Decide(facts);

        Assert.False(verdict.IsAllowed);
        Assert.Contains(says, verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows keeps a suspended app in memory only while nothing else needs the pages, so closing
    /// one buys nothing a user waited for — and a package state that will not be read is refused
    /// with it.
    /// </summary>
    [Theory]
    [InlineData(PackageAnswer.Suspended, "suspended this app")]
    [InlineData(PackageAnswer.StateUnreadable, "whether this app is suspended")]
    [InlineData(PackageAnswer.Unreadable, "app package")]
    public void APackagedApplicationThatIsNotRunningIsRefused(PackageAnswer package, string says)
    {
        var verdict = Decide(Allowed() with { Package = package });

        Assert.False(verdict.IsAllowed);
        Assert.Contains(says, verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.2's reasoning for a subject that is not a path: what has no recognised route is not
    /// offered. A set that could not be read is not a process with no windows, and both are refused.
    /// </summary>
    [Fact]
    public void AProcessWithNoWindowThatQualifiesIsRefusedRatherThanAttempted()
    {
        Assert.Contains(
            "no window of its own",
            Decide(Allowed() with { Windows = [] }).Reason,
            StringComparison.Ordinal);

        Assert.Contains(
            "would not describe this program's windows",
            Decide(Allowed() with { Windows = null }).Reason,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Deguffer closing itself would leave the close unwatched, the result unwritten and §5.6 unrun,
    /// and a process Deguffer started is Deguffer's own work.
    /// </summary>
    [Fact]
    public void DegufferAndAnythingItStartedAreRefused()
    {
        var snapshot = new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, created: 1)
            .Process(Own, 1, "Deguffer.exe", 100, created: 2)
            .Process(TargetId, Own, "started-by-deguffer.exe", 50, created: 5)
            .Build();

        Assert.Contains(
            "This is Deguffer",
            Decide(Allowed(), Own, snapshot).Reason,
            StringComparison.Ordinal);

        Assert.Contains(
            "Deguffer started this program",
            Decide(Allowed(), TargetId, snapshot).Reason,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The desktop. GetShellWindow names the shell exactly, and the shell is only "usually
    /// explorer.exe", so the names catch the rest — including the compositor, which the desktop is
    /// not a thing to stake on an expectation about.
    /// </summary>
    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("dwm.exe")]
    [InlineData("DWM.EXE")]
    public void TheDesktopIsRefusedByNameAsWellAsByTheShellWindow(string name)
    {
        var snapshot = new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, created: 1)
            .Process(Own, 1, "Deguffer.exe", 100, created: 2)
            .Process(TargetId, 1, name, 50, created: 5)
            .Build();

        Assert.Contains(
            "takes the desktop with it",
            Decide(Allowed(), TargetId, snapshot, ShellOwner.None).Reason,
            StringComparison.Ordinal);

        Assert.Contains(
            "Windows shell",
            Decide(Allowed(), Shell, snapshot).Reason,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// §2: Memory never controls a service, and a host's window is every service in it.
    /// </summary>
    [Fact]
    public void AProcessHostingAServiceIsRefused() =>
        Assert.Contains(
            "hosts a Windows service",
            Decide(Allowed(), 500).Reason,
            StringComparison.Ordinal);

    /// <summary>One row of §7.2.1's table: what makes a process match it, and what it then says.</summary>
    private sealed record Row(string Says, Func<Subject, Subject> Applies);

    /// <summary>A process as the policy is asked about it: the read it came from, its facts, the desktop.</summary>
    private sealed record Subject(MemorySnapshot Snapshot, ProcessFacts Facts, ShellOwner Shell, int ProcessId)
    {
        /// <summary>The same process, in a machine a test has rebuilt around it.</summary>
        public Subject In(int parent, string name, bool hostsService)
        {
            var snapshot = new MemorySnapshotBuilder()
                .Process(Shell.ProcessId, 1, "explorer.exe", 200, created: 1)
                .Process(Own, 1, "Deguffer.exe", 100, created: 2)
                .Process(ProcessId, parent, name, 300, created: 5);

            if (hostsService)
            {
                snapshot.Service("Thing", ProcessId);
            }

            return this with { Snapshot = snapshot.Build() };
        }
    }

    /// <summary>
    /// §7.2.1's table, in its order, with what makes a process match each row.
    ///
    /// <para>Every row here is true of one process at once: a suspended packaged program with no
    /// window of its own, hosting a service, owning a console, running as somebody else at a higher
    /// integrity level in another session, marked critical, named <c>explorer.exe</c>, and started by
    /// Deguffer. Absurd as a machine, and exactly what the ordering rule is about.</para>
    /// </summary>
    private static readonly Row[] Table =
    [
        new("another Windows session", s => s with { Facts = s.Facts with { InOwnSession = Answer.No } }),
        // Alone in hosting no service: §7.2 puts no process under a service host, so a host cannot
        // also be Deguffer's descendant, and no machine can match both rows at once.
        new("Deguffer started this program", s => s.In(Own, "explorer.exe", hostsService: false)),
        new("part of the Windows desktop", s => s.In(1, "explorer.exe", hostsService: true)),
        new("critical", s => s with { Facts = s.Facts with { Critical = Answer.Yes } }),
        new("higher integrity level", s => s with { Facts = s.Facts with { AboveOwnIntegrity = Answer.Yes } }),
        new("hosts a Windows service", s => s.In(1, "editor.exe", hostsService: true)),
        new("console window", s => s with { Facts = s.Facts with { OwnsConsoleWindow = Answer.Yes } }),
        new("suspended this app", s => s with { Facts = s.Facts with { Package = PackageAnswer.Suspended } }),
        new("no window of its own", s => s with { Facts = s.Facts with { Windows = [] } }),
    ];

    /// <summary>
    /// §7.2.1: "Where more than one row applies, the first in this order is the reason shown, so the
    /// same process always gives the same answer."
    ///
    /// <para>Each row is asked of a process that matches it and every row below it, so this proves
    /// the whole order rather than the pairs somebody thought to write down.</para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EveryRowAnswersBeforeTheRowsBelowIt(int row)
    {
        // Bottom-up, because a row that rebuilds the machine states what that row needs and the row
        // above it must have the last word.
        var subject = Table.Skip(row).Reverse().Aggregate(
            new Subject(Snapshot(), Allowed(), ShellOwner.Is(Shell), TargetId).In(1, "editor.exe", hostsService: false),
            (matching, below) => below.Applies(matching));

        var verdict = Policy().Decide(
            subject.Snapshot, Process(subject.Snapshot, subject.ProcessId), subject.Facts, subject.Shell);

        Assert.False(verdict.IsAllowed);
        Assert.Contains(Table[row].Says, verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row's reason runs both ways. A program Deguffer is running inside — a debugger, or a
    /// launcher that ends what it started — takes Deguffer with it when it goes, which leaves the
    /// close unwatched and §5.6 unrun exactly as closing Deguffer itself would.
    /// </summary>
    [Fact]
    public void AProgramDegufferIsRunningInsideIsRefused()
    {
        var snapshot = new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, created: 1)
            .Process(TargetId, Shell, "debugger.exe", 300, created: 2)
            .Process(Own, TargetId, "Deguffer.exe", 100, created: 5)
            .Build();

        Assert.Contains(
            "Deguffer is running inside this program",
            Decide(Allowed(), TargetId, snapshot).Reason,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The shell is named by its window, not by an image name, so an owner Windows will not name
    /// leaves Deguffer unable to tell whether the program in front of it is the desktop. §7.2.1
    /// refuses on an unreadable fact exactly as it refuses on one that came back wrong.
    /// </summary>
    [Fact]
    public void AShellWindowWindowsWillNotAttributeRefusesTheClose()
    {
        var verdict = Decide(Allowed(), shell: ShellOwner.Unreadable);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("which program owns the desktop", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.2: "Nothing is asked of Windows for a row nobody selected." The verdict for one row opens
    /// that row's process, asks the desktop once, and asks about nothing else.
    /// </summary>
    [Fact]
    public void TheVerdictForOneRowAsksAboutThatRowAndNothingElse()
    {
        var snapshot = Snapshot();
        var facts = new FakeProcessFactSource(Allowed());
        var desktop = new FakeDesktopFacts(Shell);

        var verdict = new MemoryActionPolicy(facts, desktop, Own).For(snapshot, Process(snapshot, TargetId));

        Assert.True(verdict.IsAllowed, verdict.Reason);
        Assert.Equal([(TargetId, 5L)], facts.Asked);
        Assert.Equal(1, desktop.Reads);
    }

    /// <summary>
    /// A process the picture could not date is a process Deguffer cannot tell from a later one
    /// holding the same identifier, and §7.2.1 identifies the target by both.
    /// </summary>
    [Fact]
    public void AProcessWithNoCreationTimeIsRefused()
    {
        var snapshot = Snapshot();
        var undated = Process(snapshot, TargetId) with { CreationTime = null };

        var verdict = Policy().Decide(snapshot, undated, Allowed(), ShellOwner.Is(Shell));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("when this process was created", verdict.Reason, StringComparison.Ordinal);
    }
}
