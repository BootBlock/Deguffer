
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
/// folder is not offered at all.</para>
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
    /// Why nothing an update left may be offered on this machine right now, or null where nothing
    /// says an update is going on.
    /// </summary>
    /// <param name="ownCalls">
    /// Deguffer's own calls into the servicing stack, for a DISM command alone, whose stack the hold
    /// then does not take for an update where <paramref name="ownCalls"/> shows Deguffer woke it. An
    /// update that reuses that stack cannot be told from it, which is safe only for DISM: Windows runs
    /// one servicing session at a time, and DISM refuses to clean while a restart is owed. Null for
    /// a removal by path, which nothing serialises against an update, so any servicing stack holds it.
    /// </param>
    public static string? HoldsEverything(IWindowsServicing servicing, IProcessInspector inspector, OwnServicingCalls? ownCalls = null)
    {
        if (servicing.IsRestartPending)
        {
            return "Windows is waiting for a restart to finish installing an update, and these may be "
                + "part of it. They are offered once Windows has restarted.";
        }

        IReadOnlyList<string> running =
        [
            .. inspector.FindRunningProcesses(Processes)
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
        // CleanupPlan.NarrowedTo makes, for the reason it gives.
        PresenceBefore: PathPresence.Present,
        HeldContentBefore: false,
        Withheld: Withholding.UpdateInProgress);
}
