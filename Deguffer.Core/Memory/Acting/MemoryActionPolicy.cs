namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// What Memory will and will not ask to close (§7.2.1).
///
/// <para>A decision, not a menu, and it lives in Core for the reason
/// <see cref="Exploring.Acting.ExploreActionPolicy"/> does: what Memory refuses has to be provable
/// without a WinUI host, and a rule that exists only as a greyed-out menu item is a rule nothing can
/// test.</para>
///
/// <para><b>Every refusal is decided twice.</b> This policy decides when the user selects a row, so
/// the reason is on screen before they try anything, and <see cref="ProcessCloser"/> decides again
/// through the handle it holds, immediately before the first message. <see cref="Decide"/> is that
/// one decision, taking the facts it judges rather than reading them, so the second decision runs
/// the same rules against what the held handle said.</para>
///
/// <para><b>Where more than one row applies, the first in §7.2.1's order is the reason shown</b>, so
/// the same process always gives the same answer. Every row is a rule rather than the consequence of
/// an access check failing: unelevated, the investigation behind §7.2.1 could open none of the twelve
/// system processes it named, so a refusal that waited for Windows to say no would depend on how much
/// Deguffer happened to be allowed.</para>
///
/// <para><b>A fact Windows would not answer refuses exactly as a fact that came back wrong does.</b>
/// <see cref="Answer.Unreadable"/> is not <see cref="Answer.No"/>, and reading it as one would let a
/// process through on a question nobody got an answer to.</para>
/// </summary>
public sealed class MemoryActionPolicy
{
    private readonly IProcessFactSource _source;
    private readonly IDesktopFacts _desktop;
    private readonly int _own;

    /// <param name="source">Where the facts §7.2.1 decides from are read, one process at a time.</param>
    /// <param name="desktop">Where the shell window's owner is read, which is not a fact about the row.</param>
    /// <param name="ownProcessId">
    /// Deguffer's own identifier. Injected so the refusal of Deguffer and its own tree is provable
    /// against a snapshot a test wrote, which is the one case that cannot be arranged on the real
    /// machine: a test cannot make this process the parent of an invented one.
    /// </param>
    public MemoryActionPolicy(IProcessFactSource source, IDesktopFacts desktop, int? ownProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(desktop);

        _source = source;
        _desktop = desktop;
        _own = ownProcessId ?? Environment.ProcessId;
    }

    /// <summary>
    /// The verdict for the one row the user selected, reading what Windows says about that process
    /// and nothing else (§7.2).
    /// </summary>
    public MemoryVerdict For(MemorySnapshot snapshot, ProcessMemory target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);

        if (target.CreationTime is not { } created)
        {
            return MemoryVerdict.Refuse(Unidentified);
        }

        return Decide(snapshot, target, _source.Read(target.ProcessId, created, ct), _desktop.ShellWindowOwner());
    }

    /// <summary>
    /// §7.2.1's refusal table, in its order, against facts the caller has already read.
    /// </summary>
    /// <param name="snapshot">
    /// The read the user picked from. It answers the two rows Windows is not asked about: which
    /// processes host a service, and what each process is called.
    /// </param>
    /// <param name="target">The process the user picked, as that snapshot describes it.</param>
    /// <param name="facts">What Windows says about it, read for this decision.</param>
    /// <param name="shell">What Windows says about the shell window's owner.</param>
    public MemoryVerdict Decide(
        MemorySnapshot snapshot,
        ProcessMemory target,
        ProcessFacts facts,
        ShellOwner shell)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(facts);

        if (target.CreationTime is null)
        {
            return MemoryVerdict.Refuse(Unidentified);
        }

        // Before the table, because every row below is about a process, and this is the question of
        // whether the process is still there to have rows applied to it.
        if (facts.Present != Answer.Yes)
        {
            return MemoryVerdict.Refuse(facts.Present == Answer.No
                ? "The program you picked has gone. Windows reuses process identifiers, so this one "
                + "now belongs to nothing, or to something else."
                : "Windows would not say whether this is still the program you picked, and Deguffer "
                + "will not act on a program it cannot identify.");
        }

        return Elsewhere(facts)
            ?? Degufferself(snapshot, target)
            ?? Desktop(target, shell)
            ?? Critical(facts)
            ?? Reachable(facts)
            ?? Hosting(snapshot, target)
            ?? Console(facts)
            ?? Packaged(facts)
            ?? Windows(facts);
    }

    private const string Unidentified =
        "Deguffer could not read when this process was created, so it cannot tell it from a later "
        + "program holding the same identifier, and it will not act on it.";

    /// <summary>
    /// Another session, or another account. A window in another session cannot be reached from this
    /// one, and another user's program is not this user's to close.
    /// </summary>
    private static MemoryVerdict? Elsewhere(ProcessFacts facts) => (facts.InOwnSession, facts.AsOwnAccount) switch
    {
        (Answer.No, _) => MemoryVerdict.Refuse(
            "This program runs in another Windows session, and a window there cannot be reached from "
            + "this one."),

        (Answer.Unreadable, _) => MemoryVerdict.Refuse(
            "Windows would not say which session this program runs in, and Deguffer only closes "
            + "programs in your own."),

        (_, Answer.No) => MemoryVerdict.Refuse(
            "This program runs as another account. Another user's program is not yours to close from "
            + "here."),

        (_, Answer.Unreadable) => MemoryVerdict.Refuse(
            "Windows would not say which account this program runs as, and Deguffer only closes "
            + "programs running as you."),

        _ => null,
    };

    /// <summary>
    /// Deguffer itself, and anything in its own process tree. Deguffer closing itself leaves the
    /// action unwatched, the result unwritten and §5.6 unrun, and a process Deguffer started is
    /// Deguffer's own work rather than something the user picked out of the machine.
    /// </summary>
    private MemoryVerdict? Degufferself(MemorySnapshot snapshot, ProcessMemory target)
    {
        if (target.ProcessId == _own)
        {
            return MemoryVerdict.Refuse(
                "This is Deguffer. Closing it would leave the close unwatched, the result unwritten "
                + "and nothing verified.");
        }

        var tree = ProcessTree.Of(snapshot);

        // Both directions of the tree, because the row's reason runs both ways. Below Deguffer is a
        // process Deguffer started, which is Deguffer's own work. Above it is a program whose exit
        // can take Deguffer with it — a debugger holding it, or a launcher that ends what it started
        // — and that leaves the close unwatched and §5.6 unrun exactly as closing Deguffer would.
        if (tree.Holds(target, _own))
        {
            return MemoryVerdict.Refuse(
                "Deguffer is running inside this program, so closing it could take Deguffer with it "
                + "and leave the close unwatched and nothing verified.");
        }

        return tree.Descends(target, _own)
            ? MemoryVerdict.Refuse(
                "Deguffer started this program itself, so it is part of Deguffer's own work rather "
                + "than something to close from here.")
            : null;
    }

    /// <summary>
    /// The desktop. <c>GetShellWindow</c> names the shell exactly, and the shell is only "usually
    /// explorer.exe", so the two image names catch the rest. The compositor is expected to be refused
    /// by its account as well, and is named outright because the desktop is not a thing to stake on
    /// an expectation.
    /// </summary>
    private static MemoryVerdict? Desktop(ProcessMemory target, ShellOwner shell)
    {
        // An unreadable answer refuses as a wrong one does: there is a shell window, and Deguffer
        // cannot tell whether this program is the program that owns it.
        if (shell.Read == Answer.Unreadable)
        {
            return MemoryVerdict.Refuse(
                "Windows would not say which program owns the desktop, so Deguffer cannot tell "
                + "whether this is it.");
        }

        if (shell.Read == Answer.Yes && target.ProcessId == shell.ProcessId)
        {
            return MemoryVerdict.Refuse(
                "This is the Windows shell: the desktop, the taskbar and your folder windows. "
                + "Closing it takes the desktop with it.");
        }

        return target.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
            || target.Name.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase)
                ? MemoryVerdict.Refuse(
                    "This is part of the Windows desktop itself. Closing it takes the desktop with it.")
                : null;
    }

    /// <summary>
    /// A critical process, and one whose criticality will not be read. Ending a critical process
    /// stops the machine with <c>CRITICAL_PROCESS_DIED</c>, and the call that answers it needs only
    /// the access Deguffer already has, so a process that will not answer it is refusing for a reason.
    /// </summary>
    private static MemoryVerdict? Critical(ProcessFacts facts) => facts.Critical switch
    {
        Answer.Yes => MemoryVerdict.Refuse(
            "Windows marks this process critical. Ending one stops the machine outright."),

        Answer.Unreadable => MemoryVerdict.Refuse(
            "Windows would not say whether this process is critical, and one that will not answer "
            + "that is not one to ask anything of."),

        _ => null,
    };

    /// <summary>
    /// A process above Deguffer's own integrity level, or one whose level will not be read. Windows
    /// filters a message posted upwards, and its own sources disagree about whether the post reports
    /// that or reports success and drops the message, so Deguffer decides before posting and never by
    /// what the post says.
    /// </summary>
    private static MemoryVerdict? Reachable(ProcessFacts facts) => facts.AboveOwnIntegrity switch
    {
        Answer.Yes => MemoryVerdict.Refuse(
            "This program runs at a higher integrity level than Deguffer, so Windows would not "
            + "deliver the close to it. Deguffer does not ask for more rights in order to close "
            + "something."),

        Answer.Unreadable => MemoryVerdict.Refuse(
            "Windows would not say what integrity level this program runs at, so Deguffer cannot "
            + "tell whether a close would reach it at all."),

        _ => null,
    };

    /// <summary>
    /// A process hosting any service (§2). A host's window is every service in it.
    ///
    /// <para><b>This is the one row that cannot be made a rule</b>, and §7.2.1 says so rather than
    /// implying a coverage it has not got: Windows leaves the services this account may not query out
    /// of the list without an error, so a host it did not name is an ordinary process here. What
    /// stands between the user and such a host is the rest of the table, and the confirmation says
    /// what the list left out.</para>
    /// </summary>
    private static MemoryVerdict? Hosting(MemorySnapshot snapshot, ProcessMemory target) =>
        snapshot.Services.Services.Any(service => service.ProcessId == target.ProcessId)
            ? MemoryVerdict.Refuse(
                "This program hosts a Windows service, and Deguffer never controls a service. Closing "
                + "its window would be stopping every service inside it.")
            : null;

    /// <summary>
    /// A process owning a console window, and any console host. Closing a console sends
    /// <c>CTRL_CLOSE_EVENT</c> to every process attached to it, and the system ends one that handles
    /// the event when its handler returns or after five seconds — none of them asked about unsaved
    /// work. The console host reports one attached process as the owner of its window, so the refusal
    /// is of the whole process rather than of the one window.
    /// </summary>
    private static MemoryVerdict? Console(ProcessFacts facts) => facts.OwnsConsoleWindow switch
    {
        Answer.Yes => MemoryVerdict.Refuse(
            "This program owns a console window. Closing a console ends every program attached to it, "
            + "and none of them is asked about unsaved work first."),

        Answer.Unreadable => MemoryVerdict.Refuse(
            "Windows would not describe one of this program's windows, so Deguffer cannot tell "
            + "whether it owns a console."),

        _ => null,
    };

    /// <summary>
    /// A packaged application that is not running. Windows keeps a suspended one in memory only while
    /// nothing else needs the pages and reclaims it when something does, so closing one buys nothing
    /// a user waited for.
    /// </summary>
    private static MemoryVerdict? Packaged(ProcessFacts facts) => facts.Package switch
    {
        PackageAnswer.Suspended => MemoryVerdict.Refuse(
            "Windows has suspended this app. It holds its memory only while nothing else needs it, "
            + "and Windows takes that back the moment something does, so closing it gains nothing."),

        PackageAnswer.StateUnreadable => MemoryVerdict.Refuse(
            "Windows would not say whether this app is suspended, and a suspended one is not worth "
            + "closing."),

        PackageAnswer.Unreadable => MemoryVerdict.Refuse(
            "Windows would not say whether this program belongs to an app package, so Deguffer "
            + "cannot tell whether it is one Windows has already suspended."),

        _ => null,
    };

    /// <summary>
    /// A process with no window that qualifies, which is §5.2's reasoning for a subject that is not a
    /// path: what has no recognised route is not offered, and a route Deguffer had to guess at is not
    /// a recognised one.
    ///
    /// <para>A packaged application whose window belongs to the frame host rather than to itself
    /// falls here, which is why there is no rule about frame hosts: the enumeration finds it no window
    /// of its own, and Deguffer does not go looking by any other route.</para>
    /// </summary>
    private static MemoryVerdict Windows(ProcessFacts facts) => facts.Windows switch
    {
        null => MemoryVerdict.Refuse(
            "Windows would not describe this program's windows, so Deguffer does not know what it "
            + "would be asking to close."),

        [] => MemoryVerdict.Refuse(
            "This program has no window of its own on screen, so there is no close of its own to "
            + "send it. Deguffer offers nothing it would have to invent a route for."),

        var windows => MemoryVerdict.Allow(windows),
    };
}
