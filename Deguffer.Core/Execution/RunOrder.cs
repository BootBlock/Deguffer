namespace Deguffer.Core.Execution;

/// <summary>
/// Which of the findings chosen for a clean it runs, and in what order.
///
/// <para>A finding with nothing to remove and nothing to prove is not part of the run, so it is
/// dropped here rather than skipped inside the run's loop. One with nothing to remove and a survivor
/// to prove stays, because verifying that survivor is the whole of what it is for.</para>
///
/// <para>Those go last, whatever order the selection arrived in: their promise is about what the
/// whole run leaves standing, and checking it before a later plan has deleted anything would prove
/// nothing about that plan. <c>OrderBy</c> is stable, so the plans that delete keep the order they
/// were given.</para>
/// </summary>
internal static class RunOrder
{
    public static IReadOnlyList<(Finding Finding, CleanupPlan Plan)> Of(IReadOnlyList<Finding> selected) =>
        [
            .. selected
                .Select(f => (Finding: f, Plan: f.Plan))
                .Where(p => p.Plan is { IsEmpty: false } or { HasSomethingToProve: true })
                .Select(p => (p.Finding, Plan: p.Plan!))
                .OrderBy(p => p.Plan.IsEmpty),
        ];
}
