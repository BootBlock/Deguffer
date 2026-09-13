using Deguffer.Core.Execution;

namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// §5.6 for a close, from the snapshot taken as the first message was posted and the one taken when
/// the watch ended (§7.2.1).
///
/// <para>A disk does not delete itself while Deguffer looks away, and processes exit constantly, so
/// "nothing else stopped" is not a claim this action can make. What it can do is say which of three
/// things each exit was, and never smooth the difference over:</para>
///
/// <list type="bullet">
/// <item><b>What the close could not have ended, and did not.</b> The shell window's owner and this
/// session's compositor. Asking an ordinary program to close cannot end the desktop, and neither of
/// those exits by itself while a user is signed in, so one of them missing is the alarm §5.6 exists
/// to raise.</item>
/// <item><b>What was expected to go.</b> The target's descendants, by the creation-time-checked
/// parent links of the snapshot before the action (§7.2). A child of a closed program exiting is the
/// program closing properly.</item>
/// <item><b>What Deguffer does not claim.</b> Every other process that was there and is not. A
/// service host is one of those: a shared host exits when its last service stops, so closing a
/// program that was a service's only client can end a host with nothing sent to it.</item>
/// </list>
///
/// <para><b>What the after snapshot cannot answer is a failure rather than a pass.</b> Identity here
/// is the identifier with the creation time, because Windows reuses identifiers, so a read whose
/// creation times are off — or one that stopped part-way through the process table — cannot say that
/// anything is still running. Filing that as evidence is the one thing that undoes §5.6, so it is
/// recorded against each process that had to survive, and no exit is listed from a read that could
/// not establish one.</para>
///
/// <para>Item 1, the record of what was posted and where, is the closer's: only the closer knows
/// which window it posted to and which process owned it at that moment.</para>
/// </summary>
public static class CloseEvidence
{
    private const string DesktopReason = "Asking a program to close cannot end the desktop.";
    private const string DescendantReason = "A child of the program that was asked to close goes with it.";
    private const string OtherExitReason = "Deguffer posted nothing to this process.";

    /// <summary>The idle process, which is not a process anything here is about.</summary>
    private const int IdleProcessId = 0;

    /// <param name="before">The machine as the first message was posted.</param>
    /// <param name="after">The machine as the watch ended.</param>
    /// <param name="target">The process the user asked to close, as <paramref name="before"/> had it.</param>
    /// <param name="desktop">
    /// What the close could not have ended: the process owning the shell window, and every <c>dwm.exe</c> in
    /// Deguffer's own session, each as <paramref name="before"/> had it. Resolved by the caller,
    /// because neither the shell window nor a session is anything a snapshot answers.
    /// </param>
    public static IReadOnlyList<VerificationCheck> Of(
        MemorySnapshot before,
        MemorySnapshot after,
        ProcessMemory target,
        IReadOnlyList<ProcessMemory> desktop)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desktop);

        var standing = StillRunning(after);
        var checks = new List<VerificationCheck>(desktop.Count + 1);

        foreach (var process in desktop)
        {
            checks.Add(Survivor(process, standing));
        }

        if (standing is null)
        {
            // Nothing below can be established from a read that cannot name what is still running,
            // and a list of exits drawn from one would be a list of processes that may all be there.
            return checks;
        }

        var descendants = DescendantsOf(before, target);

        foreach (var process in descendants)
        {
            if (!standing.Contains(Identity(process)))
            {
                checks.Add(new VerificationCheck(
                    Name(process), DescendantReason, VerificationOutcome.ExpectedExit, "Exited with it."));
            }
        }

        var named = descendants.Select(Identity).ToHashSet();
        named.Add(Identity(target));

        foreach (var process in desktop)
        {
            named.Add(Identity(process));
        }

        foreach (var process in Measured(before))
        {
            if (!named.Contains(Identity(process)) && !standing.Contains(Identity(process)))
            {
                checks.Add(new VerificationCheck(
                    Name(process),
                    OtherExitReason,
                    VerificationOutcome.UnclaimedExit,
                    "Exited while the close was watched. Processes exit on their own, and Deguffer "
                    + "does not claim this one as its doing."));
            }
        }

        return checks;
    }

    /// <summary>
    /// One process the close could not have ended, and what became of it.
    ///
    /// <para>A process the after snapshot could not be asked about is a failure and says why. The
    /// alternative — reporting it as still running — is a §5.6 line that establishes nothing while
    /// reading as though it had.</para>
    /// </summary>
    private static VerificationCheck Survivor(ProcessMemory process, HashSet<(int, long)>? standing) => standing switch
    {
        null => new VerificationCheck(
            Name(process),
            DesktopReason,
            VerificationOutcome.Failed,
            "NOT ESTABLISHED — the read taken when the watch ended could not say which processes "
            + "were still running, so nothing was checked."),

        _ when standing.Contains(Identity(process)) => new VerificationCheck(
            Name(process), DesktopReason, VerificationOutcome.Survived, "Still running."),

        _ => new VerificationCheck(
            Name(process),
            DesktopReason,
            VerificationOutcome.Failed,
            "MISSING — it was running when the close was posted, and asking a program to close "
            + "cannot end it."),
    };

    /// <summary>
    /// Every process the after snapshot can say is still running, or null where it cannot say that
    /// about any of them.
    ///
    /// <para>Null on two reads. One whose creation times are off cannot tell a process from a later
    /// one holding its identifier, which is the whole of identity here. One that stopped part-way
    /// through the process table is missing whatever came after that point, so an absence in it is
    /// not an exit.</para>
    /// </summary>
    private static HashSet<(int, long)>? StillRunning(MemorySnapshot after) =>
        after.Processes is { Figures: ProcessFigures.Checked, Complete: true } table
            ? [.. Measured(table.Processes).Select(Identity)]
            : null;

    /// <summary>
    /// Everything under <paramref name="target"/> in the snapshot before the action, by the parent
    /// links §7.2 allows.
    ///
    /// <para><see cref="ProcessForest"/> is asked rather than the recorded parent identifiers,
    /// because Windows reuses identifiers and a "parent" created after its child is not its parent.
    /// It also puts nothing under a service host, which is what keeps a broker or a packaged
    /// application Windows started from a host out of some other program's expected exits.</para>
    /// </summary>
    private static IReadOnlyList<ProcessMemory> DescendantsOf(MemorySnapshot before, ProcessMemory target)
    {
        var measured = Measured(before);
        var hosted = before.Services.Services.Select(service => service.ProcessId).ToHashSet();
        var hosts = measured.Select(process => process.ProcessId).Where(hosted.Contains).ToHashSet();
        var forest = ProcessForest.Of(measured, hosts);

        // By identity rather than by reference: the target arrived from the snapshot the user picked
        // from, which is this one, but a caller holding an equal record from anywhere else must get
        // the same answer.
        var root = measured.FirstOrDefault(process => Identity(process) == Identity(target));

        if (root is null)
        {
            return [];
        }

        var descendants = new List<ProcessMemory>();
        var pending = new Stack<ProcessMemory>();
        pending.Push(root);

        while (pending.TryPop(out var process))
        {
            foreach (var child in forest.ChildrenOf(process))
            {
                descendants.Add(child);
                pending.Push(child);
            }
        }

        return descendants;
    }

    /// <summary>
    /// The processes of one snapshot that can be identified at all: the idle process is not one, and
    /// neither is a record whose creation time the figure check turned off.
    /// </summary>
    private static IReadOnlyList<ProcessMemory> Measured(MemorySnapshot snapshot) =>
        Measured(snapshot.Processes.Processes);

    private static IReadOnlyList<ProcessMemory> Measured(IReadOnlyList<ProcessMemory> processes) =>
        [.. processes.Where(p => p.ProcessId != IdleProcessId && p.CreationTime is not null)];

    private static (int, long) Identity(ProcessMemory process) =>
        (process.ProcessId, process.CreationTime ?? 0);

    private static string Name(ProcessMemory process) => $"{process.Name} (process {process.ProcessId})";
}
