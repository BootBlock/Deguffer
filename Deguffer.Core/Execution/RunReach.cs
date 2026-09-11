namespace Deguffer.Core.Execution;

/// <summary>
/// Everything one run may destroy, gathered across every plan in it.
///
/// <para>§5.6's negative asks whether a protected path that has gone missing went missing because
/// of Deguffer, and <see cref="PlanVerifier"/> answers that by comparing the disappearance against
/// what the deletion could have reached. A run is many plans — <see cref="CleanupPlanner"/> loops
/// the selected providers and each verifies as it finishes — so a verifier holding only its own
/// plan's targets would find another provider's deletion indistinguishable from a stranger's, and
/// report a protected path Deguffer itself took as one something else removed. That is the §5.6
/// alarm suppressed by Deguffer's own hand, which is the one direction this check must never
/// fail in.</para>
///
/// <para>It is a separate value rather than a field on <see cref="CleanupPlan"/> because it belongs
/// to the run and not to any plan in it. Passing it explicitly is what stops a caller executing a
/// plan without saying what else the run will touch.</para>
/// </summary>
/// <param name="TargetedPaths">Every path the run's plans will destroy outright.</param>
/// <param name="ProbedPaths">
/// Every path a tool's own eviction command in the run is sent to clear: each
/// <see cref="RunCommandStep.MeasuredPaths"/>, gathered across the plans.
///
/// <para>Not a target, and it bounds nothing. §5.1 leaves the command deciding what it removes, so
/// <see cref="Unbounded"/> still makes every disappearance the run's to answer for. What these paths
/// are is the plan's own statement of where the tool was sent, and that is what §5.6's
/// emptied-in-place question needs: a protected folder holding one of them can end the run empty
/// because the run said it would work there. A protected folder holding none of them has no such
/// explanation, whatever else the run holds.</para>
/// </param>
/// <param name="Unbounded">
/// Whether any plan hands a tool its own eviction command, whose reach nothing here can state.
///
/// <para>§5.1 keeps that command as the preferred route precisely because the tool knows about
/// locations Deguffer does not — <c>dotnet nuget locals all --clear</c> cleared four, two of them
/// outside <c>.nuget</c> — so a run holding one has no bounded reach at all, and every disappearance
/// in it stays the run's to answer for.</para>
///
/// <para>It is asked of a disappearance and never of an emptied folder. "The run could have done
/// it" is what makes a missing path an alarm rather than an outside removal, and it makes an emptied
/// folder an alarm for the same reason, so it cannot also be the reason to excuse one. That excuse
/// is <see cref="ProbedPaths"/>'s to give, and only for the folders above where the tool was
/// sent.</para>
/// </param>
public sealed record RunReach(
    IReadOnlyList<string> TargetedPaths,
    IReadOnlyList<string> ProbedPaths,
    bool Unbounded)
{
    /// <summary>A run that will destroy nothing, for a verification with no execution behind it.</summary>
    public static readonly RunReach Nothing = new([], [], Unbounded: false);

    public static RunReach Of(IReadOnlyList<CleanupPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var commands = plans.SelectMany(plan => plan.Steps.OfType<RunCommandStep>()).ToList();

        return new RunReach(
            [.. plans.SelectMany(plan => plan.TargetedPaths)],
            [.. commands.SelectMany(command => command.MeasuredPaths)],
            Unbounded: commands.Count > 0);
    }
}
