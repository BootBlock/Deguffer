using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// §9 applied to a finished plan: an Outlook mail store the plan's measurements found is protected by
/// its path, and a step that cannot leave one behind is withheld.
///
/// <para><b>Two kinds of step, and the difference is whether the removal can step over one file.</b> A
/// removal Deguffer performs file by file steps over a store (see <see cref="RemovalWalk"/>), so its step
/// stays and does less, and its figure already excludes the store. A step that cannot leave one file is
/// withheld while a store is inside its reach: a tool's own command decides what it removes (§5.1),
/// Windows empties a Recycle Bin whole, and a removal whose subject goes whole or not at all
/// (<see cref="DeleteDirectoryStep.IsIndivisible"/>) would keep the store and take what belongs with it.
/// So is a step whose whole subject is one store.</para>
///
/// <para><b>Every store becomes a protection, whichever kind of step found it.</b> §5.6 then proves
/// each one survived, and a run that lost one fails its verification — which is what an over-broad
/// removal looks like from here. A subject withheld whole is protected too, on its contents, so a rule
/// elsewhere in the run that emptied it is caught as well.</para>
///
/// <para>Separate from <see cref="Providers.CleanupProviderBase"/>, which applies it to every plan a
/// provider returns, because the rule is the plan's and the base class is the place that must not be
/// able to forget it (G1).</para>
/// </summary>
public static class MailStorePlan
{
    /// <summary>The reason every store's protection gives, in the verification report.</summary>
    private const string Reason = "An Outlook data file, which Deguffer never removes.";

    /// <summary>The reason a subject withheld whole gives, in the verification report.</summary>
    private const string WholeReason = "Holds an Outlook data file, so Deguffer leaves it exactly as it is.";

    /// <param name="plan">The plan a provider built.</param>
    /// <param name="measured">
    /// Every store the measurements behind this plan met. A command step names only the paths it
    /// reports against, so each store here is given to every command whose reach holds it. Null or
    /// empty where the steps already carry what they found, which is every removal Deguffer performs.
    /// </param>
    public static CleanupPlan Apply(CleanupPlan plan, IEnumerable<string>? measured = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var inReach = measured?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

        IReadOnlyList<CleanupStep> steps = inReach.Count == 0
            ? plan.Steps
            : [.. plan.Steps.Select(step => step is RunCommandStep command ? WithStoresInReach(command, inReach) : step)];

        var stores = steps.SelectMany(step => step.MailStores).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (stores.Count == 0)
        {
            return plan;
        }

        var notes = new List<PlanNote>(plan.Notes);
        var kept = new List<CleanupStep>(steps.Count);
        var leftWhole = new List<ProtectedPath>();

        foreach (var step in steps)
        {
            if (WhyWithheld(plan, step) is not { } why)
            {
                kept.Add(step);
                continue;
            }

            notes.Add(new PlanNote(PlanNoteSeverity.Warning, why));

            if (step is DeleteStep whole and (EmptyRecycleBinStep or DeleteDirectoryStep { IsIndivisible: true }))
            {
                leftWhole.Add(new ProtectedPath(
                    LongPath.Display(whole.Path),
                    WholeReason,
                    // Measured during planning, and a store was found inside it, so it was there and held
                    // something when the plan was made. Its figure cannot say so, because the store is left
                    // out of every figure.
                    ExistedBefore: true,
                    HeldContentBefore: true));
            }
        }

        var leftByRemovals = kept.SelectMany(step => step.MailStores).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (leftByRemovals.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                leftByRemovals.Count == 1
                    ? $"The Outlook data file at {leftByRemovals[0]} is inside what this removes, and is left "
                      + "where it is, because Deguffer never removes one. The sizes here already exclude it, and "
                      + "every clean checks that it is still there."
                    : $"What this removes holds {Every(leftByRemovals)}. They are left where they are, because "
                      + "Deguffer never removes one. The sizes here already exclude them, and every clean checks "
                      + "that they are still there."));
        }

        return plan with
        {
            Steps = kept,
            ProtectedPaths = [.. plan.ProtectedPaths, .. ProtectionsFor(stores), .. leftWhole],
            Notes = notes,
        };
    }

    /// <summary>
    /// The first store by its path, and how many more there are, for a sentence reporting what a run or
    /// an action refused, which has to say which file stopped it without listing a folder's worth of
    /// them. A preview note names every store instead; see <see cref="Every"/>.
    /// </summary>
    public static string Name(IReadOnlyList<string> stores)
    {
        ArgumentNullException.ThrowIfNull(stores);

        return stores.Count switch
        {
            0 => string.Empty,
            1 => stores[0],
            _ => $"{stores[0]} and {stores.Count - 1} more",
        };
    }

    /// <summary>
    /// Every store by its full path, for a preview note. The preview is the one place a reader learns
    /// which of their files Deguffer leaves or refuses around, and nothing else on screen lists the paths
    /// a plan protects, so none of them is left out. Separated by semicolons, because a path may hold a
    /// comma.
    /// </summary>
    private static string Every(IReadOnlyList<string> stores) => stores.Count switch
    {
        1 => $"an Outlook data file, at {stores[0]}",
        _ => $"{stores.Count} Outlook data files, at {string.Join("; ", stores.SkipLast(1))} and {stores[^1]}",
    };

    /// <summary>
    /// One protection per store, each on its existence alone. A store is a file, so there is no
    /// content to ask about, and a spelling that differs only in case is the same file.
    /// </summary>
    private static IEnumerable<ProtectedPath> ProtectionsFor(IEnumerable<string> stores) =>
        stores
            .Select(LongPath.Display)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(store => new ProtectedPath(
                store,
                Reason,
                // Measured during planning, so it was there when the plan was made — the claim
                // CleanupPlan.NarrowedTo makes, for the reason it gives.
                ExistedBefore: true,
                HeldContentBefore: false,
                Withheld: Withholding.MailStore));

    /// <summary>
    /// Why this step cannot run while it holds a store, or null where it can. Only a step that cannot
    /// leave one file behind, or whose whole subject is a store, is withheld.
    /// </summary>
    private static string? WhyWithheld(CleanupPlan plan, CleanupStep step) => step switch
    {
        { MailStores.Count: 0 } => null,

        RunCommandStep command =>
            $"Not running {plan.ProviderName}'s own command ({Path.GetFileName(command.FileName)} "
            + $"{command.Arguments}): what it clears holds {Every(command.MailStores)}. The tool decides "
            + "for itself what it removes and cannot be told to leave one file, and Deguffer never removes "
            + "one. Move what is named here somewhere else, and preview again.",

        EmptyRecycleBinStep bin =>
            $"Leaving the Recycle Bin at {LongPath.Display(bin.Path)} as it is: it holds "
            + $"{Every(bin.MailStores)}. Windows empties a bin whole, and Deguffer never removes one. "
            + "Restore what is named here, or delete it from the Recycle Bin yourself, and preview again.",

        DeleteDirectoryStep { IsIndivisible: true } whole =>
            $"Leaving {LongPath.Display(whole.Path)} as it is: it holds {Every(whole.MailStores)}. What is "
            + "inside it goes whole or not at all, and Deguffer never removes one. Move what is named here "
            + "out, or delete it yourself, and preview again.",

        DeleteFileStep file =>
            $"Leaving {LongPath.Display(file.Path)} alone: it is an Outlook data file, and Deguffer never "
            + "removes one.",

        _ => null,
    };

    /// <summary>
    /// A command step with every measured store inside the paths it reports against added to what it
    /// carries. Those paths are the plan's own statement of where the tool is sent (see
    /// <see cref="RunCommandStep.MeasuredPaths"/>), so a store inside one is inside the tool's reach.
    /// </summary>
    private static RunCommandStep WithStoresInReach(RunCommandStep command, IReadOnlyList<string> measured)
    {
        var reached = measured
            .Where(store => command.MeasuredPaths.Any(path =>
                LongPath.Contains(LongPath.Display(path), LongPath.Display(store))))
            .ToList();

        return reached.Count == 0
            ? command
            : command with
            {
                MailStores = [.. command.MailStores.Union(reached, StringComparer.OrdinalIgnoreCase)],
            };
    }
}
