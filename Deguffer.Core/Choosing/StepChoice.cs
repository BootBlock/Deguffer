using Deguffer.Core.Execution;

namespace Deguffer.Core.Choosing;

/// <summary>
/// Whether one step of a preview can be ticked, and whether it starts ticked.
///
/// <para>Here rather than in the shell so the rule is provable without a window, and without a
/// test host whose own token decides the answer. Which rights this process holds is passed in: the
/// step's declaration is a fact about the location and the token is a fact about the process, so a
/// plan describes the disk the same way whoever asks, and the pairing of the two is made here, once.</para>
///
/// <para>A step that cannot be ticked is never left ticked. Its checkbox is disabled, so a tick on it
/// would be a selection the user has no way to clear, and the checkboxes that stand for several steps
/// pass it over. That is why the starting tick asks the same question as the checkbox.</para>
/// </summary>
public static class StepChoice
{
    /// <summary>
    /// Whether <paramref name="step"/> is one Deguffer can see and cannot remove as it is running.
    /// Shown beside the step rather than left to fail at execution time, where it would explain
    /// nothing. <see cref="ElevationOffer"/> reads the same declaration.
    /// </summary>
    public static bool NeedsElevationFirst(CleanupStep step, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.RequiresElevation && !isElevated;
    }

    /// <summary>
    /// Nothing to reclaim means nothing to choose, and neither does a step this process has no
    /// rights to carry out, nor an item the user keeps.
    ///
    /// <para>"Nothing to reclaim" is <see cref="CleanupStep.RemovesSomething"/>'s answer rather than a
    /// byte test, so a leftover of empty folders can be chosen and an empty cache folder its tool
    /// re-creates still cannot. A kept item is not in its row's plan at all, so a tick on it would
    /// count towards the selected total and remove nothing.</para>
    /// </summary>
    /// <param name="isKept">Whether the keep list took this step out of its row's plan.</param>
    public static bool CanBeSelected(CleanupStep step, bool isKept, bool isElevated) =>
        !NeedsElevationFirst(step, isElevated) && !isKept && step.RemovesSomething;

    /// <summary>
    /// Whether a step starts ticked: where its row's remembered or §3 default says so, and only
    /// where it can be ticked at all. A kept item is never pre-selected, whatever the row says.
    /// </summary>
    /// <param name="rowSays">
    /// What <see cref="Configuration.SelectionMemory"/> answers for this step, which already carries
    /// §3's default for a row the user has never touched.
    /// </param>
    public static bool StartsSelected(CleanupStep step, bool rowSays, bool isKept, bool isElevated) =>
        rowSays && CanBeSelected(step, isKept, isElevated);

    /// <summary>
    /// Whether a row can be ticked: whether any step it offers can be.
    ///
    /// <para>Asked of the steps rather than of the finding's total, because the row's checkbox is a
    /// shorthand for ticking every step in it. Where nothing in the row can be ticked, ticking it would
    /// tick nothing and leave a row that says it is selected and removes nothing. The Windows servicing
    /// logs are that case: every step of theirs needs administrator rights.</para>
    /// </summary>
    /// <param name="offered">
    /// The finding with the keep list already applied, so every step in its plan is one the row
    /// offers. See <see cref="CleanupPlan.WithKeepList"/>.
    /// </param>
    public static bool AnyCanBeSelected(Finding offered, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(offered);

        return offered.Plan?.Steps.Any(step => CanBeSelected(step, isKept: false, isElevated)) == true;
    }
}
