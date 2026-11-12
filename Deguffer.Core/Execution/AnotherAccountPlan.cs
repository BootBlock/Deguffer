using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Withholds every step that belongs to the account this process runs as, while that account is not
/// confirmed as the user signed in to Windows. See <see cref="RunningAccount"/> for why, and for why
/// the steps are withheld rather than pointed at the signed-in user's folders.
///
/// <para><b>A step belongs to the account</b> when it reaches one of the account's folders (see
/// <see cref="RunningAccount.Folders"/>) or its folder in a Recycle Bin, or when it acts on the calling
/// account's own state wherever its path is (<see cref="CleanupStep.ActsForCallingAccount"/>). What
/// the whole machine shares reaches none of those, and is left as the provider planned it.</para>
///
/// <para>Applied by the planner to every plan, beside <see cref="VirtualMachineStoragePlan"/> and for
/// the same reason: the decision is the run's, and no provider has to remember it. One instance for a
/// planning pass, so each of the account's folders is followed to every path it is reachable at once,
/// not once for every step.</para>
/// </summary>
public sealed class AnotherAccountPlan
{
    private const string RecycleBinFolder = "$Recycle.Bin";

    private readonly RunningAccount _account;
    private readonly IVolumeInventory _volumes;
    private readonly IReadOnlyList<ReachedFolder> _folders;

    private AnotherAccountPlan(RunningAccount account, IVolumeInventory volumes)
    {
        _account = account;
        _volumes = volumes;
        _folders = [.. account.Folders.Select(folder => RegisteredStorage.Reach(folder, volumes))];
    }

    /// <summary>
    /// The check for one planning pass, or null where the account is the signed-in user's and every plan
    /// stands as it was made.
    /// </summary>
    public static AnotherAccountPlan? For(RunningAccount account, IVolumeInventory volumes)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(volumes);

        return account.IsSignedInUser ? null : new AnotherAccountPlan(account, volumes);
    }

    /// <summary>
    /// <paramref name="plan"/> without the steps that belong to the account, each of their subjects
    /// protected instead (§5.6) and a note saying whose they are.
    /// </summary>
    public CleanupPlan Apply(CleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var withheld = plan.Steps.Where(BelongsToAccount).ToList();

        if (withheld.Count == 0)
        {
            return plan;
        }

        return plan with
        {
            Steps = [.. plan.Steps.Except(withheld)],
            ProtectedPaths = [.. plan.ProtectedPaths, .. withheld.SelectMany(Proofs)],
            Notes = [.. plan.Notes, new PlanNote(PlanNoteSeverity.Warning, WhyWithheld(withheld.Count))],
        };
    }

    /// <summary>
    /// What a withheld step leaves for §5.6 to prove standing, so every withheld step leaves something:
    /// a row whose steps were all withheld then reads as holding another account's files rather than as
    /// already clear, and a run proves them standing.
    /// </summary>
    private IEnumerable<ProtectedPath> Proofs(CleanupStep step)
    {
        var reason = $"Belongs to {_account.Name}, the account Deguffer is running as, so Deguffer left it alone.";
        var marker = (step as RunCommandStep)?.Scheduled?.Marker;

        if (step.Subjects.Count > 0)
        {
            // Measured during planning, so it was there when the plan was made, and its content asked of
            // the disk: the claims CleanupPlan.NarrowedTo makes for a step the user declined, for the
            // reasons it gives. The content is what proves a bin standing, because a call that empties one
            // leaves its folder in place.
            return step.Subjects.Select(path => new ProtectedPath(
                path,
                reason,
                PresenceBefore: PathPresence.Present,
                DirectoryContent.IsPresent(path),
                Withheld: Withholding.AnotherAccount,
                Marker: marker));
        }

        // A tool's command names no subject and a cloud release destroys nothing, so where each works is
        // what is left standing. Asked of the disk, because a command's measured paths are probes and
        // need not all exist. On existence alone: the tool that owns a cache goes on writing to it and
        // trimming it, so a question about its content would cry wolf about a folder nothing in the run
        // touched.
        IReadOnlyList<string> places = step is ReleaseLocalCopiesStep release ? [release.SyncRoot] : step.Reaches;

        return places.Select(path => new ProtectedPath(
            path,
            reason,
            LongPath.ProbeEntry(path),
            HeldContentBefore: false,
            Withheld: Withholding.AnotherAccount,
            Marker: marker));
    }

    private bool BelongsToAccount(CleanupStep step) =>
        step.ActsForCallingAccount || step.Reaches.Any(path => IsAccountFolder(path) || IsAccountBin(path));

    /// <summary>
    /// Inside one of the account's folders, or holding one. Followed to every path the folder is
    /// reachable at, so a step naming it through another drive letter or mount still meets it.
    /// </summary>
    private bool IsAccountFolder(string path)
    {
        var reached = RegisteredStorage.Reach(path, _volumes);

        return _folders.Any(folder => folder.Holds(reached) || reached.Holds(folder));
    }

    /// <summary>
    /// Inside the account's own folder of a Recycle Bin, <c>&lt;volume&gt;\$Recycle.Bin\&lt;SID&gt;</c>.
    /// Matched on the two names rather than on the volume's root, so a bin on a volume mounted in a
    /// folder is recognised as surely as one at a drive's root.
    /// </summary>
    private bool IsAccountBin(string path)
    {
        if (_account.SecurityIdentifier is not { } identifier)
        {
            return false;
        }

        for (var current = LongPath.Display(path); Path.GetDirectoryName(current) is { } parent; current = parent)
        {
            if (string.Equals(Path.GetFileName(current), identifier, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(parent), RecycleBinFolder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string WhyWithheld(int count)
    {
        var (items, them, they) = count == 1
            ? ("One item here belongs", "it", "it is")
            : ($"{count} items here belong", "them", "they are");
        var whose = _account.Standing == AccountStanding.AnotherAccount
            ? $"not to {_account.SignedIn?.Name}, who is signed in to Windows"
            : "and Windows would not say whether that is the account signed in to it";

        return $"{items} to {_account.Name}, the account Deguffer is running as, {whose}. Deguffer leaves "
            + $"{them} alone, and every clean checks that {they} still there. Open Deguffer as yourself, "
            + "without administrator rights, to clean your own.";
    }
}
