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

        // The same weighting the planner applies to whole plans, for the same reason: one obj
        // directory of 4 GB and five of 20 MB are six steps, and splitting the bar six ways would
        // crawl through the first sixth and then jump the rest.
        var weights = ProgressWeights.For(plan.Steps.Select(s => s.EstimatedBytes));
        var total = weights.Sum();
        var done = 0.0;

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var step = plan.Steps[i];

            // Each step's own 0-to-1 becomes its slice of this plan's 0-to-1.
            var stepProgress = ScaledProgress.Within(progress, done / total, weights[i] / total);

            outcomes.Add(step switch
            {
                RunCommandStep command => await RunCommandAsync(command, ct).ConfigureAwait(false),
                DeleteDirectoryStep delete => await DeleteAsync(delete, plan.Keep, stepProgress, ct).ConfigureAwait(false),
                DeleteFileStep delete => await DeleteAsync(delete, plan.Keep, stepProgress, ct).ConfigureAwait(false),
                _ => throw new NotSupportedException($"Unknown step type {step.GetType().Name}."),
            });

            // Reported from here rather than trusted from the step: a command step reports nothing
            // at all while it runs, and a removal that ends early would leave a gap that never
            // closes.
            done += weights[i];
            progress?.Report(done / total);
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
        //
        // MeasuredBefore wins where it is present, because the delta must subtract like from
        // like: a step whose estimate is the tool's own figure re-measures paths that never held
        // that number. See its declaration.
        //
        // The two sides are commensurable because ScanSize.Reclaimable is Logical, which both of
        // §5.5's routes measure and measure identically. This subtraction was the first place that
        // mattered — the before-figure can come from the file table and the after-figure never does
        // — and it is no longer a special case: see ScanSize.Reclaimable for why the whole tool
        // reports that axis.
        var before = (step.MeasuredBefore ?? step.Estimated).Reclaimable;

        var outcome = await runner.RunAsync(step.FileName, step.Arguments, ct).ConfigureAwait(false);

        // From the disk, never from the volume snapshot. Nothing invalidates that snapshot between
        // planning and executing — Invalidate runs once, at the top of a planning pass — so an
        // ordinary measurement here would hand back the very figure it is about to be subtracted
        // from, and a clean that freed gigabytes would report nothing.
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
        MinimumAge keep,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var removal = await DirectoryRemover.RemoveAsync(step.Path, keep, progress, ct).ConfigureAwait(false);

        // Skipped items are not a failure (§5.3). The step only fails if the directory survived
        // intact and nothing at all was reclaimed — that is, we achieved nothing.
        //
        // A file the guard held back counts as something achieved, because it is the outcome the
        // user asked for. A directory holding nothing else reclaims no bytes and keeps its root, so
        // without this the setting working exactly as intended would be reported as a failed step.
        var succeeded = removal.RootRemoved || removal.BytesReclaimed > 0 || removal.Kept > 0;

        var message = succeeded switch
        {
            false => WhyNothingHappened(removal.Skipped),

            // Nothing came out and the folder is still standing, which is the guard's own case: it
            // held back everything this step named. Saying "Removed" here would be a false statement
            // about the user's disk, and the qualifier would not rescue it — the sentence has to be
            // about what stayed, because that is all that happened.
            _ when removal is { BytesReclaimed: 0, RootRemoved: false } =>
                $"Left alone: {removal.Kept} file(s) changed too recently.",

            _ => $"Removed{Qualifier(removal.Skipped, removal.Kept)}.",
        };

        return new StepOutcome(
            step.Description,
            succeeded,
            removal.BytesReclaimed,
            removal.Skipped,
            message,
            removal.Kept);
    }

    private static async Task<StepOutcome> DeleteAsync(
        DeleteFileStep step,
        MinimumAge keep,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var removal = await FileRemover.RemoveAsync(step.Path, keep, ct).ConfigureAwait(false);

        // One file, so there is no fraction to report along the way — only the end of it.
        progress?.Report(1.0);

        // Kept is a success for the same reason it is on a directory: nothing was removed because
        // nothing was meant to be. There is only the one file, so the whole message says so rather
        // than qualifying a removal that did not happen.
        if (removal.Kept)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: true,
                BytesReclaimed: 0,
                Skipped: 0,
                "Left alone: it changed too recently.",
                Kept: 1);
        }

        var message = removal.Removed ? "Removed." : WhyNothingHappened(removal.Skipped);

        return new StepOutcome(
            step.Description, removal.Removed, removal.BytesReclaimed, removal.Skipped, message);
    }

    /// <summary>
    /// What a successful removal has to add about what it left behind, or nothing where it left
    /// nothing. The two causes are named separately because they ask different things of the
    /// reader: one is a process they can close, and the other is a setting they chose.
    /// </summary>
    private static string Qualifier(int skipped, int kept) => (skipped, kept) switch
    {
        (0, 0) => string.Empty,
        (_, 0) => $", {skipped} item(s) left in place because they were in use",
        (0, _) => $", {kept} file(s) left alone because they changed recently",
        _ => $", {skipped} item(s) left in place because they were in use and "
             + $"{kept} because they changed recently",
    };

    /// <summary>
    /// The sentence for a deletion that achieved nothing.
    ///
    /// It names no cause, and that is the decision rather than an omission. The two available
    /// causes are indistinguishable from here — an unelevated delete under the Windows directory is
    /// refused file by file, which arrives as exactly the skip a locked file produces — and naming
    /// either would be a guess. Naming <em>both</em> was tried and is worse: the shell does not
    /// offer a step needing administrator rights to a process that has none, so this is reached
    /// almost only on an elevated run, where "run as administrator" is advice the reader has
    /// already taken. The plan carries what needs administrator rights; this reports what happened.
    /// </summary>
    private static string WhyNothingHappened(int skipped) => skipped == 0
        ? "Nothing was removed."
        : $"Nothing was removed: Windows would not release {skipped} item(s).";

    private async Task<long> MeasureAllAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var total = ScanSize.Zero;

        foreach (var path in paths)
        {
            var measured = await scanner.MeasureFromDiskAsync(path, ct).ConfigureAwait(false);
            total += measured.Size;
        }

        return total.Reclaimable;
    }
}
