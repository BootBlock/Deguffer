
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Whether the update that left something behind is still going on, for the providers that remove
/// what an upgrade or an update leaves at the top of the system drive.
///
/// <para><b>A hold, never a warning.</b> Everywhere else §5.3's running process is a note beside a
/// plan that still runs, because a locked file is left where it is and nothing is lost. Here the
/// danger is not a locked file. A folder an unfinished update is still going to read — the rollback
/// state it restores from, the files a restart will move into place — can be entirely unlocked, and
/// removing it interrupts the update rather than failing to. So while any of these answers yes, the
/// folder is not offered at all. Deguffer's own servicing session is the one exception: a row is
/// offered beside it and its clean waits for it to stop (see <see cref="OnlyOwnServicing"/>).</para>
/// </summary>
internal static class UnfinishedUpdate
{
    /// <summary>
    /// The processes that are Windows servicing itself or upgrading itself: the servicing stack, and
    /// the setup engine an upgrade or the update assistant runs.
    /// </summary>
    public static readonly IReadOnlyList<string> Processes =
    [
        "TiWorker", "TrustedInstaller", "SetupHost", "SetupPrep", "SetupPlatform", "WindowsUpdateBox",
    ];

    /// <summary>
    /// The servicing stack, which Windows also starts for any DISM call, Deguffer's own included.
    /// The setup engine is never started that way, so it always holds.
    /// </summary>
    private static readonly HashSet<string> ServicingStack = new(["TiWorker", "TrustedInstaller"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why nothing an update left may be offered on this machine, as <paramref name="state"/> describes
    /// it, or null where nothing says an update is going on.
    /// </summary>
    /// <param name="state">
    /// A planning pass's one reading (<see cref="PlanningServicing"/>), or the machine read afresh by a
    /// clean.
    /// </param>
    /// <param name="ownCalls">
    /// Deguffer's own calls into the servicing stack, for a DISM command alone, whose stack the hold
    /// then does not take for an update where <paramref name="ownCalls"/> shows Deguffer woke it, and
    /// whose session key it does not take for a restart owed where Deguffer's call wrote it (see
    /// <see cref="OwnServicingCalls.Wrote"/>). An update that reuses that stack cannot be told from it,
    /// which is safe only for DISM: Windows runs one servicing session at a time, and DISM refuses to
    /// clean while a restart is owed. Null for anything else, which nothing serialises against an
    /// update, so any servicing stack or restart key holds it. A plan asks with the record and a clean
    /// waits for that stack to stop: see <see cref="OnlyOwnServicing"/>.
    /// </param>
    public static string? HoldsEverything(ServicingState state, OwnServicingCalls? ownCalls = null)
    {
        if (state.Restart.Marks.Any(mark => ownCalls?.Wrote(mark) is not true))
        {
            return "Windows is waiting for a restart to finish installing an update, and these may be "
                + "part of it. They are offered once Windows has restarted.";
        }

        IReadOnlyList<string> running =
        [
            .. state.Running
                .Where(process => !(ownCalls is not null && ServicingStack.Contains(process.Name) && process.StartedAt is { } started && ownCalls.Started(started)))
                .Select(process => process.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        return running.Count > 0
            ? $"Windows is installing an update now ({string.Join(", ", running)} "
              + $"{(running.Count == 1 ? "is" : "are")} running), and these may be part of it. Scan again "
              + "once it has finished."
            : null;
    }

    /// <summary>
    /// Whether the only thing that holds <paramref name="state"/> is Deguffer's own servicing session:
    /// the servicing stack one of <paramref name="ownCalls"/> woke, which Windows keeps running for
    /// minutes after the call returns, or the session key it wrote. That stack is not an update, but it
    /// cannot be told from one an update would reuse, so a removal that is not a DISM command waits for
    /// it to stop rather than running beside it (see <see cref="UpdateHoldAtClean"/>).
    /// </summary>
    public static bool OnlyOwnServicing(ServicingState state, OwnServicingCalls ownCalls) =>
        HoldsEverything(state) is not null && HoldsEverything(state, ownCalls) is null;

    /// <summary>
    /// The plan's sentence where <see cref="OnlyOwnServicing"/> is true, for a row offered on the
    /// understanding that the clean waits.
    /// </summary>
    public const string WaitsForOwnServicing =
        "Windows' servicing stack, which a component store command of Deguffer's own started, is still "
        + "running. The clean waits for it to stop, which usually takes a few minutes, before it "
        + "removes any of this.";

    /// <summary>The run's sentence where Deguffer's own servicing session outlasted the wait for it.</summary>
    /// <param name="waited">How long the run waited.</param>
    public static string OwnServicingOutlasted(TimeSpan waited) =>
        $"Nothing was removed: Windows' servicing stack, which a component store command of Deguffer's "
        + $"own started, was still running after {(int)waited.TotalMinutes} minutes, and an update may be using it. "
        + "Scan again once it has stopped.";

    /// <summary>The sentence for one folder a restart will change something inside.</summary>
    public static string PendingIn(string path) =>
        $"Leaving {LongPath.Display(path)} alone: Windows will change something inside it at the next "
        + "restart, so the update that wrote it has not finished.";

    /// <summary>The same for a run that reached a folder a restart is now due to change.</summary>
    public static string PendingNow(string path) =>
        $"Nothing was removed: Windows will change something inside {LongPath.Display(path)} at the next "
        + "restart, so the update that wrote it has not finished.";

    /// <summary>
    /// A folder held back because an update has not finished. Protected on its existence alone, for
    /// the reason a kept item is (see <see cref="CleanupPlan.WithKeepList"/>): the update that owns it
    /// goes on working in it, and only Deguffer's removal of it is the subject.
    /// </summary>
    public static ProtectedPath Held(string path) => new(
        path,
        "Left alone because the Windows update that wrote it has not finished.",
        // Found during planning, so it was there when the plan was made — the claim
        // ChosenSteps makes, for the reason it gives.
        PresenceBefore: PathPresence.Present,
        HeldContentBefore: false,
        Withheld: Withholding.UpdateInProgress);
}
