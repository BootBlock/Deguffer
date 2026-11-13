using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// A plan divided by the steps the user ticked: the steps that run, and the subject of every step the
/// user declined, which the narrowed plan protects.
///
/// <para><b>Narrowing lives in Core rather than in the shell</b> because of what it has to do besides
/// drop steps: every deletion the user declined becomes a protected path. §5.6's negative is the
/// promise that a step which did not run left its subject standing, and after per-item selection the
/// deselected directory is a sibling of the selected one — same parent, same shape — which is exactly
/// when an over-broad rule takes both. A shell that narrowed a plan by filtering
/// <see cref="CleanupPlan.Steps"/> itself would silently drop that guarantee, so the only narrowing
/// available adds it.</para>
///
/// <para>A dropped <see cref="RunCommandStep"/> contributes no protection unless it names the one item
/// it removes: its <see cref="RunCommandStep.MeasuredPaths"/> are a probe rather than a target (§5.1),
/// and asserting the tool left them alone would be asserting something this plan never controlled. See
/// <see cref="CleanupStep.Subjects"/>.</para>
///
/// <para><b>Divided without the disk.</b> Built on the UI thread when Clean is pressed, from the ticks
/// only that thread can read. What each declined subject held is asked of the disk afterwards, by
/// <see cref="PlanNarrowing"/>, off that thread.</para>
/// </summary>
public sealed class ChosenSteps
{
    private readonly IReadOnlyList<CleanupStep> _selected;
    private readonly IReadOnlyList<string?> _markers;

    /// <param name="chosen">The steps of <paramref name="plan"/> the user ticked.</param>
    public ChosenSteps(CleanupPlan plan, IReadOnlyCollection<CleanupStep> chosen)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(chosen);

        Plan = plan;

        // Named for the user's choice rather than for what is kept, because "keep" already means the
        // guard on recently changed files and the keep list elsewhere in this project, and each of
        // those decides something different about the same plan.
        //
        // Asked of a set built here, because the shell passes a list and a row of thousands of steps
        // is ordinary: a list's Contains is a linear search of record equality, which on that row is
        // millions of comparisons. Built with the default comparer even when the caller passes a set,
        // so a set comparing some other way cannot change which steps are kept and which are declined.
        var chosenSet = new HashSet<CleanupStep>(chosen);
        _selected = [.. plan.Steps.Where(chosenSet.Contains)];

        var declined = _selected.Count == plan.Steps.Count
            ? []
            : plan.Steps.Except(_selected).SelectMany(step => step.Subjects.Select(path => (Path: path, Marker: CleanupPlan.MarkerOf(step)))).ToList();

        DeclinedSubjects = [.. declined.Select(subject => subject.Path)];
        _markers = [.. declined.Select(subject => subject.Marker)];
    }

    /// <summary>The plan the steps were chosen from.</summary>
    public CleanupPlan Plan { get; }

    /// <summary>The subject of every declined step, in the order the plan gives its steps.</summary>
    internal IReadOnlyList<string> DeclinedSubjects { get; }

    /// <summary>
    /// <see cref="Plan"/> narrowed to the chosen steps, with each declined subject protected.
    /// <see cref="Plan"/> itself where every step was chosen.
    /// </summary>
    /// <param name="heldContent">
    /// What <see cref="DirectoryContent.IsPresent"/> answered for each of <see cref="DeclinedSubjects"/>,
    /// in their order, asked of the disk when Clean was pressed.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="heldContent"/> does not answer every declined subject.</exception>
    internal CleanupPlan Narrowed(ReadOnlySpan<bool> heldContent)
    {
        if (heldContent.Length != DeclinedSubjects.Count)
        {
            throw new ArgumentException(
                $"{DeclinedSubjects.Count} declined subjects need an answer each, and {heldContent.Length} were given.",
                nameof(heldContent));
        }

        if (_selected.Count == Plan.Steps.Count)
        {
            return Plan;
        }

        var declined = new ProtectedPath[DeclinedSubjects.Count];

        for (var index = 0; index < declined.Length; index++)
        {
            declined[index] = new ProtectedPath(
                DeclinedSubjects[index],
                "Left alone because it was not selected for this run.",
                // It was measured during planning, so it was there when the plan was made. That is
                // the only claim PresenceBefore makes, and re-probing the disk here would let a
                // directory deleted between planning and execution excuse itself.
                PresenceBefore: PathPresence.Present,

                // Content is asked of the disk rather than assumed, which is the opposite of the
                // line above and for a reason the two do not share. Assuming it would report every
                // declined directory that was *already* empty as having been emptied by the run —
                // an alarm about an untouched folder, on a machine where an unused drive's bin is
                // ordinary. The excuse the probe allows is harmless in the other direction: a
                // directory something else emptied in the gap has nothing left for an over-broad
                // rule to destroy. Asked when Clean is pressed rather than taken from planning, so
                // the answer describes the folder the run is about to stand beside.
                //
                // This is the site the declined Recycle Bin depends on. A bin the user unticked is
                // still standing after a call that emptied it anyway, so existence proves nothing
                // and this is the whole of what §5.6 has left to compare.
                heldContent[index],
                Marker: _markers[index]);
        }

        return Plan with
        {
            Steps = _selected,
            ProtectedPaths = [.. Plan.ProtectedPaths, .. declined],
        };
    }
}
