using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// The run-wide refusal of a virtual machine's storage, applied to a finished plan: a step that would
/// reach a registered disk or folder is withheld, and each one it would have reached is protected, so
/// §5.6 proves afterwards that the run left it.
///
/// <para><b>Withheld whole, whatever kind of step it is.</b> A folder holding a registered disk is where
/// its tool keeps that machine, and what lies beside the disk is the tool's too. A tool's own command
/// decides for itself what it removes (§5.1), and Windows clears a bin or a handler's folders whole. So
/// nothing that reaches one runs, rather than running and stepping over the file the way a removal steps
/// over an Outlook data file (<see cref="MailStorePlan"/>). A step inside a registered folder is withheld
/// for the same reason.</para>
///
/// <para><b>Every clean proves every one.</b> <see cref="ProofFor"/> gives each registering row a plan
/// that protects all it registered, and <see cref="CleanupPlanner.ExecuteAsync"/> runs it last in every
/// clean, whichever rows were scanned or chosen. That is the negative <c>unreached-locations.md</c> §11
/// found missing: before this, a report had no step, no run included it, and Explore's refusal was the
/// only guard.</para>
///
/// <para>Applied by <see cref="CleanupPlanner"/> to every plan it builds, because the set belongs to the
/// pass and no single row can see the others' records (G1).</para>
/// </summary>
public static class VirtualMachineStoragePlan
{
    /// <summary>The reason a whole subject withheld gives, in the verification report.</summary>
    private const string WholeReason = "Holds a virtual machine's storage, so Deguffer leaves it exactly as it is.";

    /// <param name="plan">The plan a row built.</param>
    /// <param name="registered">Every registration the pass read.</param>
    /// <param name="volumes">Asked where else each step's path is reachable, as the registrations were.</param>
    public static CleanupPlan Apply(CleanupPlan plan, RegisteredStorage registered, IVolumeInventory volumes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(volumes);

        var standing = registered.Standing.ToList();

        if (standing.Count == 0 || plan.Steps.Count == 0)
        {
            return plan;
        }

        var reached = new List<RegisteredEntry>();
        var leftWhole = new List<ProtectedPath>();
        var notes = new List<PlanNote>(plan.Notes);
        var kept = new List<CleanupStep>(plan.Steps.Count);

        foreach (var step in plan.Steps)
        {
            var reach = step.Reaches.Select(path => (Path: path, Folder: RegisteredStorage.Reach(path, volumes))).ToList();
            var inReach = standing
                .Where(entry => reach.Any(path => path.Folder.Holds(entry.Reached) || entry.Reached.Holds(path.Folder)))
                .ToList();

            if (inReach.Count == 0)
            {
                kept.Add(step);
                continue;
            }

            reached.AddRange(inReach);
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, WhyWithheld(plan, step, inReach)));

            if (step is DeleteStep or RemoveProfileStep)
            {
                // Each path the step would have destroyed that holds the storage, protected on its
                // contents, so a rule elsewhere in the run that emptied it is caught as well. A path
                // inside a registered folder is not one: the folder's own protection covers it.
                leftWhole.AddRange(reach
                    .Where(path => inReach.Any(entry => path.Folder.Holds(entry.Reached) && !path.Folder.IsSameAs(entry.Reached)))
                    .Select(path => new ProtectedPath(
                        LongPath.Display(path.Path),
                        WholeReason,
                        // Measured during planning, and the storage inside it was standing when the pass
                        // began, so it was there and held something when the plan was made.
                        PresenceBefore: PathPresence.Present,
                        HeldContentBefore: true)));
            }
        }

        if (reached.Count == 0)
        {
            return plan;
        }

        return plan with
        {
            Steps = kept,
            ProtectedPaths = [.. plan.ProtectedPaths, .. ProtectionsFor(reached), .. leftWhole],
            Notes = notes,
        };
    }

    /// <summary>
    /// A plan for <paramref name="registrar"/> that does nothing and proves every disk and folder it
    /// registered is still standing once a clean is over, or null where none is standing. See
    /// <see cref="CleanupPlanner.ExecuteAsync"/>.
    /// </summary>
    public static CleanupPlan? ProofFor(ICleanupProvider registrar, RegisteredStorage registered)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(registered);

        var own = registered.Standing.Where(entry => entry.RegisteredBy == registrar.Id).ToList();

        return own.Count == 0
            ? null
            : new CleanupPlan
            {
                ProviderId = registrar.Id,
                ProviderName = registrar.Name,
                Tier = registrar.Tier,
                WhatHappensOnNextUse = registrar.WhatHappensOnNextUse,
                ProtectedPaths = [.. ProtectionsFor(own)],
            };
    }

    /// <summary>
    /// One protection per disk or folder, each on its existence alone, with the presence Windows gave
    /// when it was read. A disk is a file, so there is no content to ask about, and a machine's folder changes as
    /// the machine runs, so asking whether it still holds what it held would cry wolf.
    /// </summary>
    private static IEnumerable<ProtectedPath> ProtectionsFor(IEnumerable<RegisteredEntry> entries) =>
        entries
            .DistinctBy(entry => entry.Storage.Path, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new ProtectedPath(
                entry.Storage.Path,
                $"The storage of {entry.Storage.Owner}, which Deguffer never removes.",
                entry.Presence,
                HeldContentBefore: false,
                Withheld: Withholding.VirtualMachineStorage));

    private static string WhyWithheld(CleanupPlan plan, CleanupStep step, IReadOnlyList<RegisteredEntry> inReach)
    {
        var named = Named(inReach);

        return step switch
        {
            RunCommandStep command =>
                $"Not running {plan.ProviderName}'s own command ({Path.GetFileName(command.FileName)} "
                + $"{command.Arguments}): where it works holds {named}. The tool decides for itself what it "
                + "removes, and deleting that storage deletes the machine and everything in it.",

            RemoveProfileStep profile =>
                $"Leaving the profile at {LongPath.Display(profile.Folder)} as it is: it holds {named}. Windows "
                + "removes a profile whole, and deleting that storage deletes the machine and everything in it.",

            DeleteStep whole =>
                $"Leaving {LongPath.Display(whole.Path)} as it is: it holds {named}. Deleting that storage "
                + "deletes the machine and everything in it, so Deguffer never removes it or anything beside it.",

            _ => $"Leaving this alone: it reaches {named}, which Deguffer never removes.",
        };
    }

    /// <summary>
    /// Each disk or folder by whose it is and where, for a preview note. The preview is the one place a
    /// reader learns why a row they expected to clean was held back, so none is left out. Separated by
    /// semicolons, because a path may hold a comma.
    /// </summary>
    private static string Named(IReadOnlyList<RegisteredEntry> entries) =>
        string.Join("; ", entries
            .DistinctBy(entry => entry.Storage.Path, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"the storage of {entry.Storage.Owner}, at {entry.Storage.Path}"));
}
