using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.2.1's refusal table, row by row and in its order.
///
/// <para>Every row is proven with the reason that row states, because §7.2.1 makes a refusal a
/// sentence on the row rather than a disabled button: a user who picked a process out of a picture
/// of memory and found nothing to press would learn nothing about which of eleven reasons
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

    private static MemoryActionPolicy Policy(ProcessFacts? facts = null, int? shellOwner = Shell) =>
        new(new FakeProcessFactSource(facts ?? Allowed()), new FakeDesktopFacts(shellOwner), Own);

    private static MemoryVerdict Decide(
        ProcessFacts facts, int target = TargetId, MemorySnapshot? snapshot = null, int? shellOwner = Shell)
    {
        var machine = snapshot ?? Snapshot();

        return Policy().Decide(machine, Process(machine, target), facts, shellOwner);
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
            Decide(Allowed(), TargetId, snapshot, shellOwner: null).Reason,
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

    /// <summary>
    /// §7.2.1: where more than one row applies, the first in the table's order is the reason shown,
    /// so the same process always gives the same answer rather than one that depends on which check
    /// happened to run first.
    /// </summary>
    [Fact]
    public void TheFirstRowInTheTablesOrderIsTheReasonShown()
    {
        var facts = Allowed() with
        {
            InOwnSession = Answer.No,
            Critical = Answer.Yes,
            OwnsConsoleWindow = Answer.Yes,
            Windows = [],
        };

        Assert.Contains("another Windows session", Decide(facts).Reason, StringComparison.Ordinal);

        // And with the session row satisfied, the next row that applies answers, rather than the
        // last one to be asked.
        Assert.Contains(
            "critical",
            Decide(facts with { InOwnSession = Answer.Yes }).Reason,
            StringComparison.Ordinal);
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

        var verdict = Policy().Decide(snapshot, undated, Allowed(), Shell);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("when this process was created", verdict.Reason, StringComparison.Ordinal);
    }
}
