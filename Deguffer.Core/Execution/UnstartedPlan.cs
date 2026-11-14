using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>A plan the clean was cancelled before starting.</summary>
internal static class UnstartedPlan
{
    /// <summary>
    /// Its result: no steps, and the §5.6 check of everything it protects. An earlier plan's over-reach
    /// is what could have taken one of those paths, and in a run that finished this plan's own
    /// verification would have been what said so.
    /// </summary>
    public static async Task<CleanupResult> ProveAsync(
        Finding finding,
        CleanupPlan plan,
        RunReach reach,
        RunResidue residue) => new()
    {
        ProviderId = plan.ProviderId,
        ProviderName = plan.ProviderName,
        Interrupted = true,
        Verification = await finding.Provider
            .VerifyAsync(plan, reach, residue, CancellationToken.None)
            .ConfigureAwait(false),
    };
}
