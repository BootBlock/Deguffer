using Deguffer.Core.Execution;

namespace Deguffer.Core.Choosing;

/// <summary>
/// What each entry of the Storage page's selection menu does to one row.
///
/// <para>Here rather than in the shell so the rule is provable without a window, as
/// <see cref="StepChoice"/> is. The shell says whether the row is on screen and applies the answer
/// through the row's own checkbox, so a bulk tick reaches exactly the steps a click on the row
/// would.</para>
///
/// <para><b>Only a row on screen is ticked.</b> A filter can hide a row, and a tick on a row the reader
/// cannot see would add it to the next clean unread. A clear reaches every row, hidden or not, because
/// it is the direction that removes less.</para>
///
/// <para><b>A row with a tick is never ticked again.</b> A row stays ticked while any item in it is, so
/// a ticked row may hold items the reader unticked one by one to keep them. Ticking it again would
/// tick those items back with nothing on the row to show it, and the next clean would delete them.
/// A click on a ticked row's checkbox clears it rather than filling it, for the same reason.</para>
///
/// <para><b>Select all takes Tier 3 with the rest.</b> Ticking a row is the reader's own choice, which
/// §3's "never pre-selected" does not govern. The row still says what is permanent, and Clean still
/// asks before it deletes one wherever the reader left a confirmation on (§7).</para>
/// </summary>
public static class BulkSelectionRule
{
    /// <summary>What <paramref name="selection"/> does to the row offering <paramref name="offered"/>.</summary>
    /// <param name="offered">
    /// The finding with the keep list already applied, so every step in its plan is one the row offers.
    /// See <see cref="CleanupPlan.WithKeepList"/>.
    /// </param>
    /// <param name="isListed">
    /// Whether the row is drawn in the list, rather than hidden by a filter or folded into the group
    /// below it (<see cref="PreviewPlace.Listed"/>).
    /// </param>
    /// <param name="isTicked">Whether any step in the row is ticked now.</param>
    /// <param name="isElevated">Whether this process holds administrator rights, which some steps need.</param>
    public static RowTick For(BulkSelection selection, Finding offered, bool isListed, bool isTicked, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(offered);

        return selection switch
        {
            BulkSelection.All => isListed && !isTicked && HasBytesToTick(offered, isElevated) ? RowTick.Tick : RowTick.Leave,
            BulkSelection.Recommended => !(isListed && offered.IsPreSelectedByDefault) ? RowTick.Clear
                : isTicked ? RowTick.Leave
                : RowTick.Tick,
            BulkSelection.None => RowTick.Clear,
            _ => throw new ArgumentOutOfRangeException(nameof(selection), selection, "Not a bulk selection."),
        };
    }

    /// <summary>
    /// Whether a step the row can tick gives back bytes. A row of nothing but empty folders is left
    /// alone: it can be ticked by hand, and "all" is read as everything with a size.
    /// </summary>
    private static bool HasBytesToTick(Finding offered, bool isElevated) =>
        offered.Plan?.Steps.Any(step =>
            StepChoice.CanBeSelected(step, isKept: false, isElevated) && step.Reclaim.Reclaimable > 0) == true;
}
