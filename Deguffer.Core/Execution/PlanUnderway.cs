using Deguffer.Core.Providers;

namespace Deguffer.Core.Execution;

/// <summary>
/// The row a clean has just started on, reported before its first step so a page can show which row
/// the time is being spent on, and how far through that row the run is.
/// </summary>
/// <param name="Provider">Whose plan it is, which is what a page's row is a row of.</param>
/// <param name="Stage">Whether the plan removes anything, or only proves what it promised survived.</param>
/// <param name="From">Where the plan starts, as a fraction of the whole run's progress.</param>
/// <param name="Share">How much of the whole run's progress the plan accounts for.</param>
public sealed record PlanUnderway(ICleanupProvider Provider, PlanStage Stage, double From, double Share)
{
    /// <summary>
    /// <paramref name="plan"/>, reached by a run that will carry it out: removing if it has steps, and
    /// only checking if it has none.
    /// </summary>
    public static PlanUnderway Of(ICleanupProvider provider, CleanupPlan plan, double from, double share) =>
        new(provider, plan.IsEmpty ? PlanStage.Checking : PlanStage.Removing, from, share);

    /// <summary>
    /// A plan the clean was cancelled before starting, which is only checked, and takes no share of
    /// the bar: the run moves the bar only for the plans it carries out.
    /// </summary>
    public static PlanUnderway Unstarted(ICleanupProvider provider, double from) =>
        new(provider, PlanStage.Checking, from, Share: 0);

    /// <summary>
    /// How far through this plan the run is, from 0 to 1, given how far through the whole run it is.
    /// The inverse of how the plan's own progress is scaled into the run's, so a plan that reports
    /// half its work done reads back as half done.
    ///
    /// <para>A plan with no share of the run, which is one checked after a cancellation or one
    /// estimated at nothing among others that are not, is done as soon as it is reached: it has no
    /// span of the bar to cross.</para>
    /// </summary>
    public double Within(double overall) =>
        Share > 0 ? Math.Clamp((overall - From) / Share, 0, 1) : 1;
}
