using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// §5.6 for one kind of step whose work the paths a plan protects cannot prove: what must still stand
/// after it ran. <see cref="PlanVerifier"/> asks each proof it was given of the steps it proves, so a new
/// kind of step brings its proof with its route rather than a new branch of the verifier.
///
/// <para>Built only in this assembly, for the reason <see cref="StepRoute"/> is.</para>
/// </summary>
public abstract class StepProof
{
    private protected StepProof()
    {
    }

    /// <summary>Whether this proof is what a verification asks about <paramref name="step"/>.</summary>
    internal abstract bool Proves(CleanupStep step);

    /// <summary>The checks for <paramref name="steps"/>, every one of which this proof <see cref="Proves"/>.</summary>
    /// <param name="steps">The plan's steps this proof proves, in the plan's order, of which there is at least one.</param>
    /// <param name="missing">How the verification judges a path it no longer finds, as a protected path.</param>
    internal abstract IEnumerable<VerificationCheck> Checks(
        IReadOnlyList<CleanupStep> steps,
        Func<ProtectedPath, VerificationCheck> missing,
        CancellationToken ct);
}

/// <summary>A <see cref="StepProof"/> for steps of type <typeparamref name="TStep"/>.</summary>
public abstract class StepProof<TStep> : StepProof
    where TStep : CleanupStep
{
    private protected StepProof()
    {
    }

    internal sealed override bool Proves(CleanupStep step) => step is TStep;

    internal sealed override IEnumerable<VerificationCheck> Checks(
        IReadOnlyList<CleanupStep> steps,
        Func<ProtectedPath, VerificationCheck> missing,
        CancellationToken ct) =>
        Checks([.. steps.Cast<TStep>()], missing, ct);

    private protected abstract IEnumerable<VerificationCheck> Checks(
        IReadOnlyList<TStep> steps,
        Func<ProtectedPath, VerificationCheck> missing,
        CancellationToken ct);
}
