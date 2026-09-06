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
/// <param name="Unbounded">
/// Whether any plan hands a tool its own eviction command, whose reach nothing here can state.
///
/// §5.1 keeps that command as the preferred route precisely because the tool knows about locations
/// Deguffer does not — <c>dotnet nuget locals all --clear</c> cleared four, two of them outside
/// <c>.nuget</c> — so a run holding one has no bounded reach at all, and every disappearance in it
/// stays the run's to answer for.
/// </param>
public sealed record RunReach(IReadOnlyList<string> TargetedPaths, bool Unbounded)
{
    /// <summary>A run that will destroy nothing, for a verification with no execution behind it.</summary>
    public static readonly RunReach Nothing = new([], Unbounded: false);

    public static RunReach Of(IReadOnlyList<CleanupPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        return new RunReach(
            [.. plans.SelectMany(plan => plan.TargetedPaths)],
            plans.Any(plan => plan.Steps.OfType<RunCommandStep>().Any()));
    }
}
