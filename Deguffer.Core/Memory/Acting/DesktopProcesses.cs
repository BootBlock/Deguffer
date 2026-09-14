namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// What §7.2.1's second §5.6 assertion is about, and what could not be established about it.
/// </summary>
/// <param name="Processes">
/// The processes the close could not have ended: the shell window's owner and this session's
/// compositor, each as the read taken before the action had it.
/// </param>
/// <param name="Unestablished">
/// What should have been in <paramref name="Processes"/> and could not be named. Carried rather than
/// dropped: an assertion nobody could make is not an assertion that passed, and a close whose one
/// exact negative was never built has to say so.
/// </param>
internal sealed record DesktopSet(IReadOnlyList<ProcessMemory> Processes, IReadOnlyList<string> Unestablished);

/// <summary>
/// Finds the desktop in the read taken before a close (§7.2.1).
///
/// <para>Asking an ordinary program to close cannot end the shell or the compositor, and neither
/// exits by itself while a user is signed in, so their survival is exact and a run that loses one
/// fails. Every other process the refusal table protects is deliberately not here: Deguffer's own
/// tree and an <c>explorer.exe</c> that is not the shell exit on their own in ordinary use, and a
/// watch with no deadline would turn that into a false alarm.</para>
///
/// <para><b>The shell is confirmed by creation time, not taken by identifier.</b> Its identifier
/// comes from Windows now and the record comes from the read a moment earlier, and a shell that
/// restarted in between would otherwise be asserted about the wrong process — or, worse, reported
/// missing when nothing was wrong.</para>
/// </summary>
internal static class DesktopProcesses
{
    /// <summary>The image Windows gives the compositor, one process of which runs per session.</summary>
    private const string CompositorName = "dwm.exe";

    private const string TheShell = "the process owning the shell window";
    private const string TheCompositor = "this session's compositor";

    /// <param name="before">The read taken as the action began, which is where both are named from.</param>
    /// <param name="shell">What Windows says about the shell window's owner.</param>
    /// <param name="processes">
    /// Where each candidate is opened, to confirm the shell's identity and to ask a compositor which
    /// session it runs in. One or two processes are opened here, at the moment of the action and
    /// never for a row nobody selected (§7.2).
    /// </param>
    public static DesktopSet Of(
        ProcessTree before,
        ShellOwner shell,
        IProcessCalls processes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(processes);

        var found = new List<ProcessMemory>();
        var unestablished = new List<string>();

        if (shell.Read == Answer.Yes && Confirmed(before.Holder(shell.ProcessId), processes) is { } owner)
        {
            found.Add(owner);
        }
        else if (shell.Read != Answer.No)
        {
            // Windows named an owner this read does not hold, would not name one at all, or would not
            // confirm the one it named. Any of those leaves the shell unasserted.
            unestablished.Add(TheShell);
        }

        var ownSession = processes.Own().SessionId;
        var compositors = 0;

        foreach (var candidate in before.Measured)
        {
            ct.ThrowIfCancellationRequested();

            if (!candidate.Name.Equals(CompositorName, StringComparison.OrdinalIgnoreCase)
                || InAnotherSession(candidate, ownSession, processes))
            {
                continue;
            }

            found.Add(candidate);
            compositors++;
        }

        if (compositors == 0)
        {
            unestablished.Add(TheCompositor);
        }

        return new DesktopSet(found, unestablished);
    }

    /// <summary>
    /// The record, once the process holding its identifier is the process the record describes.
    /// Null where the read did not hold it, where it will not open, or where the process now holding
    /// the identifier was created at another moment.
    /// </summary>
    private static ProcessMemory? Confirmed(ProcessMemory? record, IProcessCalls processes)
    {
        if (record is null)
        {
            return null;
        }

        var opening = processes.Open(record.ProcessId);

        if (opening.Process is not { } process)
        {
            return null;
        }

        using (process)
        {
            return process.CreationTime() == record.CreationTime ? record : null;
        }
    }

    /// <summary>
    /// Whether Windows says outright that this process belongs to another session.
    ///
    /// <para>A session that will not answer keeps the process, because a fact nobody established is
    /// not grounds for dropping a process out of the one assertion this action can make. Only the
    /// session is asked, through the handle it takes to ask: the whole fact set would open the same
    /// process to enumerate every window on the desktop, for one field.</para>
    /// </summary>
    private static bool InAnotherSession(ProcessMemory candidate, uint? ownSession, IProcessCalls processes)
    {
        if (ownSession is not { } ours)
        {
            return false;
        }

        var opening = processes.Open(candidate.ProcessId);

        if (opening.Process is not { } process)
        {
            return false;
        }

        using (process)
        {
            return process.SessionId() is { } theirs && theirs != ours;
        }
    }
}
