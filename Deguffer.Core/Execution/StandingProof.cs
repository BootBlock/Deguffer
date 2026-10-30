namespace Deguffer.Core.Execution;

/// <summary>
/// What a run owes the rows it is not going to clean: proof that what they leave standing whatever the
/// user ticks is still standing afterwards. That is the items on the keep list, and the Outlook data
/// files Deguffer never removes (§9).
///
/// <para>Both are checked by every clean (§5.6). A row that runs checks its own, because
/// <see cref="CleanupPlan.NarrowedTo"/> keeps them protected. Every other row still owes that check:
/// a row left unticked, a row whose every item is kept and so can never be ticked, and a row whose
/// confirmation was declined. An over-broad rule somewhere else in the run is exactly what could take
/// one of those.</para>
///
/// <para>Here rather than in the shell so the rule is provable without a window, as
/// <see cref="ConfirmationRequirement"/> is. The declined row is the case that went unchecked while it
/// lived in the shell, and nothing there could hold it to a test.</para>
/// </summary>
public static class StandingProof
{
    /// <param name="previewed">Every row's finding, with the keep list applied.</param>
    /// <param name="running">The findings the run will carry out, narrowed to what was ticked.</param>
    /// <returns>
    /// For each row that is not running and holds kept items or Outlook data files, its plan reduced to
    /// those alone: no steps, nothing to confirm, and a promise the planner checks once every deletion
    /// is done.
    /// </returns>
    public static IReadOnlyList<Finding> For(IEnumerable<Finding> previewed, IEnumerable<Finding> running)
    {
        ArgumentNullException.ThrowIfNull(previewed);
        ArgumentNullException.ThrowIfNull(running);

        // Matched by provider rather than by finding: the run carries each row's plan narrowed to its
        // ticks, which is a different plan from the one the row previewed, and a row is one provider.
        var runningProviders = running.Select(f => f.Provider.Id).ToHashSet(StringComparer.Ordinal);

        List<Finding> owed = [];

        foreach (var finding in previewed)
        {
            if (!runningProviders.Contains(finding.Provider.Id)
                && finding.Plan is { } plan
                && (plan.HoldsKeepListItems || plan.HoldsMailStores))
            {
                owed.Add(finding with { Plan = plan.ProofOnly() });
            }
        }

        return owed;
    }
}
