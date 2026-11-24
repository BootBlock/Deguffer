namespace Deguffer.Core.Execution;

/// <summary>
/// How <see cref="PlanExecutor"/> carries out one kind of <see cref="CleanupStep"/>, and what it can
/// prove about it afterwards. The executor looks each step's route up by the step's type, so a new kind
/// of step is a new route and never an edit to the executor or the verifier.
///
/// <para><b>Built only in this assembly.</b> The members are internal and the constructor is private
/// protected, so the set of routes is the set this assembly defines. A route is still public because
/// <see cref="Providers.CleanupProviderBase"/>, a public type, takes the routes of the provider that
/// derives from it.</para>
///
/// <para><b>Never defaulted where it acts on the machine.</b> The routes that empty a Recycle Bin, ask
/// Windows' Disk Cleanup handlers, release cloud files, or remove restore points or profiles are built
/// by the provider that plans such a step, on the collaborator it is given, which is the real one only
/// where the app composes its providers (<see cref="CleanupPlanner.CreateDefault"/>). An executor given
/// none refuses a plan that holds such a step before any of its steps runs, so a test that forgot a fake
/// fails closed.</para>
/// </summary>
public abstract class StepRoute
{
    private protected StepRoute()
    {
    }

    /// <summary>The type of step this route carries out. Exactly one route of an executor carries each.</summary>
    internal abstract Type Carries { get; }

    /// <summary>
    /// What a §5.6 verification asks after this route ran, or null where the paths the plan protects are
    /// the whole of what it must prove.
    /// </summary>
    internal virtual StepProof? Proof => null;

    /// <summary>Carry out <paramref name="step"/>, which is of the type <see cref="Carries"/> names.</summary>
    internal abstract Task<StepOutcome> RunAsync(StepRun run, CleanupStep step, CancellationToken ct);
}

/// <summary>A <see cref="StepRoute"/> for steps of type <typeparamref name="TStep"/>.</summary>
public abstract class StepRoute<TStep> : StepRoute
    where TStep : CleanupStep
{
    private protected StepRoute()
    {
    }

    internal sealed override Type Carries => typeof(TStep);

    internal sealed override Task<StepOutcome> RunAsync(StepRun run, CleanupStep step, CancellationToken ct) =>
        CarryOutAsync(run, (TStep)step, ct);

    private protected abstract Task<StepOutcome> CarryOutAsync(StepRun run, TStep step, CancellationToken ct);
}
