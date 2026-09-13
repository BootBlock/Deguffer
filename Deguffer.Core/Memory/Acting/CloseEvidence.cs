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

        var descendants = ProcessTree.Under(before, target);

        foreach (var process in descendants)
        {
            if (!standing.Contains(ProcessTree.Identity(process)))
            {
                checks.Add(new VerificationCheck(
                    process.Named, DescendantReason, VerificationOutcome.ExpectedExit, "Exited with it."));
            }
        }

        var named = descendants.Select(ProcessTree.Identity).ToHashSet();
        named.Add(ProcessTree.Identity(target));

        foreach (var process in desktop)
        {
            named.Add(ProcessTree.Identity(process));
        }

        foreach (var process in ProcessTree.Measured(before))
        {
            if (!named.Contains(ProcessTree.Identity(process))
                && !standing.Contains(ProcessTree.Identity(process)))
            {
                checks.Add(new VerificationCheck(
                    process.Named,
                    OtherExitReason,
                    VerificationOutcome.UnclaimedExit,
                    "Exited while the close was watched. Processes exit on their own, and Deguffer "
                    + "does not claim this one as its doing."));
            }
        }

        return checks;
    }

    /// <summary>
    /// One window Deguffer posted to, which is the exact half of §5.6: it belonged to the target at
    /// the moment of posting, checked through the held handle, and nothing but <c>WM_CLOSE</c> was
    /// posted to it.
    ///
    /// <para><paramref name="taken"/> is what Windows said of the post, and it is recorded rather
    /// than acted on. Microsoft's own sources disagree about whether a post the integrity filter
    /// blocks reports failure or reports success and drops the message, which is why §7.2.1 decides
    /// before posting and never by what the post reports.</para>
    /// </summary>
    public static VerificationCheck Posted(ProcessWindow window, ProcessMemory target, bool taken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);

        return new VerificationCheck(
            $"window 0x{window.Handle:X} ({window.ClassName}) of {target.Named}",
            "Deguffer posted this window a close, and nothing else anywhere.",
            VerificationOutcome.Sent,
            taken
                ? "WM_CLOSE posted. The window belonged to this process at that moment, asked again "
                  + "through the handle Deguffer holds."
                : "WM_CLOSE posted, and Windows reported that it did not take it. The window belonged "
                  + "to this process at that moment, and nothing further was sent.");
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
            process.Named,
            DesktopReason,
            VerificationOutcome.Failed,
            "NOT ESTABLISHED — the read taken when the watch ended could not say which processes "
            + "were still running, so nothing was checked."),

        _ when standing.Contains(ProcessTree.Identity(process)) => new VerificationCheck(
            process.Named, DesktopReason, VerificationOutcome.Survived, "Still running."),

        _ => new VerificationCheck(
            process.Named,
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
        after.Processes is { Figures: ProcessFigures.Checked, Complete: true }
            ? [.. ProcessTree.Measured(after).Select(ProcessTree.Identity)]
            : null;

}
