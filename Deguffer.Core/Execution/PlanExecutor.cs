using System.Diagnostics;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Carries out a plan. Holds no knowledge of any cache — it dispatches the steps a provider
/// already decided on, and reports what happened.
/// </summary>
public sealed class PlanExecutor(IProcessRunner runner, IDirectoryScanner scanner)
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
                DeleteFileStep delete => await DeleteAsync(delete, stepProgress, ct).ConfigureAwait(false),
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
        // tree to learn it again would double the cost of the operation.
        //
        // This is sound only because a plan-time figure is always freshly measured. Remembered
        // sizes exist (ScanEstimateCache) but are never returned from a measurement — they only
        // reach the screen while the real scan runs. Were a stale one to arrive here it would not
        // merely look wrong: it would inflate the reclaimed total reported below.
        var before = step.EstimatedBytes;

        var outcome = await runner.RunAsync(step.FileName, step.Arguments, ct).ConfigureAwait(false);

        var after = await MeasureAllAsync(step.MeasuredPaths, ct).ConfigureAwait(false);
        var reclaimed = before - after;

        // A negative delta means the tree grew between preview and clean — a build restoring
        // packages in the background, most likely. Report what is actually still there rather
        // than clamping to zero and claiming nothing was reclaimed.
        var message = reclaimed < 0
            ? $"{outcome.Message} (the cache grew since the preview; " +
              $"{Scanning.FreeSpace.Format(after)} remains)"
            : outcome.Message;

        return new StepOutcome(
            step.Description,
            outcome.Succeeded,
            BytesReclaimed: Math.Max(0, reclaimed),
            Skipped: 0,
            message);
    }

    private static async Task<StepOutcome> DeleteAsync(
        DeleteDirectoryStep step,
        IProgress<double> progress,
        CancellationToken ct)
    {
        var removal = await DirectoryRemover.RemoveAsync(step.Path, progress, ct).ConfigureAwait(false);

        // Skipped items are not a failure (§5.3). The step only fails if the directory survived
        // intact and nothing at all was reclaimed — that is, we achieved nothing.
        var succeeded = removal.RootRemoved || removal.BytesReclaimed > 0;

        var message = succeeded
            ? removal.Skipped == 0
                ? "Removed."
                : $"Removed, {removal.Skipped} item(s) left in place because they were in use."
            : WhyNothingHappened(step, removal.Skipped);

        return new StepOutcome(step.Description, succeeded, removal.BytesReclaimed, removal.Skipped, message);
    }

    private static async Task<StepOutcome> DeleteAsync(
        DeleteFileStep step,
        IProgress<double> progress,
        CancellationToken ct)
    {
        var removal = await FileRemover.RemoveAsync(step.Path, ct).ConfigureAwait(false);

        // One file, so there is no fraction to report along the way — only the end of it.
        progress.Report(1.0);

        var message = removal.Removed ? "Removed." : WhyNothingHappened(step, removal.Skipped);

        return new StepOutcome(
            step.Description, removal.Removed, removal.BytesReclaimed, removal.Skipped, message);
    }

    /// <summary>
    /// The sentence for a deletion that achieved nothing.
    ///
    /// A step declared as needing administrator rights says so, because from the outcome alone the
    /// two causes are the same thing: an unelevated delete is refused per file, and that arrives as
    /// exactly the skip a locked file produces. The §5.3 wording on its own would send the user
    /// looking for a process to close that does not exist, so both are named and neither is
    /// asserted. The shell does not offer such a step unelevated, so reaching here means something
    /// went round that — and an outcome nobody can act on is the worst thing to report.
    /// </summary>
    private static string WhyNothingHappened(DeleteStep step, int skipped)
    {
        var what = skipped == 0
            ? "Nothing was removed."
            : $"Nothing was removed: {skipped} item(s) were left in place.";

        return step.RequiresElevation
            ? what + " This location needs administrator rights, and without them every item is "
                   + "refused — which looks exactly like something holding them open."
            : what;
    }

    private async Task<long> MeasureAllAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var total = ScanSize.Zero;

        foreach (var path in paths)
        {
            var measured = await scanner.MeasureAsync(path, progress: null, ct).ConfigureAwait(false);
            total += measured.Size;
        }

        return total.Reclaimable;
    }
}
