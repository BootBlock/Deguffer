using System.Diagnostics;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Carries out a plan. Holds no knowledge of any cache — it dispatches the steps a provider
/// already decided on, and reports what happened.
/// </summary>
public sealed class PlanExecutor(IProcessRunner runner)
{
    public async Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcomes = new List<StepOutcome>(plan.Steps.Count);

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var step = plan.Steps[i];
            var stepProgress = new Progress<double>(fraction =>
                progress?.Report((i + fraction) / plan.Steps.Count));

            outcomes.Add(step switch
            {
                RunCommandStep command => await RunCommandAsync(command, ct).ConfigureAwait(false),
                DeleteDirectoryStep delete => await DeleteAsync(delete, stepProgress, ct).ConfigureAwait(false),
                _ => throw new NotSupportedException($"Unknown step type {step.GetType().Name}."),
            });

            progress?.Report((double)(i + 1) / plan.Steps.Count);
        }

        stopwatch.Stop();

        return new CleanupResult
        {
            ProviderId = plan.ProviderId,
            ProviderName = plan.ProviderName,
            Steps = outcomes,
            Duration = stopwatch.Elapsed,

            // §5.6 is not a separate user action: acting and proving what survived are one step.
            Verification = PlanVerifier.Verify(plan, ct),
        };
    }

    private async Task<StepOutcome> RunCommandAsync(RunCommandStep step, CancellationToken ct)
    {
        // The "before" size was measured when the plan was built; re-walking a multi-gigabyte
        // tree to learn the same number would double the cost of the operation.
        var before = step.EstimatedBytes;

        var outcome = await runner.RunAsync(step.FileName, step.Arguments, ct).ConfigureAwait(false);

        var after = await MeasureAllAsync(step.MeasuredPaths, ct).ConfigureAwait(false);

        return new StepOutcome(
            step.Description,
            outcome.Succeeded,
            BytesReclaimed: Math.Max(0, before - after),
            Skipped: 0,
            outcome.Message);
    }

    private static async Task<StepOutcome> DeleteAsync(
        DeleteDirectoryStep step,
        IProgress<double> progress,
        CancellationToken ct)
    {
        var removal = await DirectoryRemover.RemoveAsync(step.Path, progress, ct).ConfigureAwait(false);

        var message = removal.Skipped == 0
            ? "Removed."
            : $"Removed, {removal.Skipped} item(s) left in place because they were in use.";

        // Skipped items are not a failure (§5.3). The step only fails if the directory survived
        // intact and nothing at all was reclaimed — that is, we achieved nothing.
        var succeeded = removal.RootRemoved || removal.BytesReclaimed > 0;

        return new StepOutcome(step.Description, succeeded, removal.BytesReclaimed, removal.Skipped, message);
    }

    private static async Task<long> MeasureAllAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        long total = 0;
        foreach (var path in paths)
        {
            total += await DirectorySizer.MeasureAsync(path, ct).ConfigureAwait(false);
        }

        return total;
    }
}
