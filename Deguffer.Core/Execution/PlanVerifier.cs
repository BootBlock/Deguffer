using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// §5.6 — assert that the things that should have survived did.
///
/// This is policy, not mechanism: what counts as evidence of survival is a safety decision, and
/// it lives in one place so it can be read and changed without touching execution.
/// </summary>
public static class PlanVerifier
{
    public static VerificationResult Verify(CleanupPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var checks = new List<VerificationCheck>(plan.ProtectedPaths.Count);

        foreach (var protectedPath in plan.ProtectedPaths)
        {
            ct.ThrowIfCancellationRequested();
            checks.Add(Check(protectedPath));
        }

        return new VerificationResult { Checks = checks };
    }

    private static VerificationCheck Check(ProtectedPath protectedPath)
    {
        // A path that was never there cannot be evidence of survival. Recording it as a pass with
        // an honest detail keeps the report from overstating what the run actually established.
        if (!protectedPath.ExistedBefore)
        {
            return new VerificationCheck(
                protectedPath.Path,
                protectedPath.Reason,
                Passed: true,
                "Not present before the clean; nothing to preserve.");
        }

        var survives = LongPath.FileExists(protectedPath.Path) || LongPath.DirectoryExists(protectedPath.Path);

        return new VerificationCheck(
            protectedPath.Path,
            protectedPath.Reason,
            survives,
            survives ? "Still present." : "MISSING — it was there before the clean.");
    }
}
