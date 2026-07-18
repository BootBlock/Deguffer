using System.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Execution and verification are identical across providers — the knowledge lives in the plan,
/// not in how it is carried out. Subclasses supply rules; this class runs them.
/// </summary>
public abstract class CleanupProviderBase : ICleanupProvider
{
    protected CleanupProviderBase(IUserEnvironment environment, IProcessRunner runner, IProcessInspector inspector)
    {
        Environment = environment;
        Runner = runner;
        Inspector = inspector;
    }

    protected IUserEnvironment Environment { get; }

    protected IProcessRunner Runner { get; }

    protected IProcessInspector Inspector { get; }

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract SafetyTier Tier { get; }

    public abstract string WhatHappensOnNextUse { get; }

    /// <summary>
    /// Processes that, if running, mean this tool's state may be live (§5.3). Their presence
    /// produces a warning on the plan rather than blocking it — a build server running npm is
    /// not a reason to refuse, but it is a reason to say so.
    /// </summary>
    protected virtual IReadOnlyList<string> ConflictingProcessNames => [];

    public abstract Task<bool> IsPresentAsync(CancellationToken ct = default);

    public abstract Task<CleanupPlan> PlanAsync(CancellationToken ct = default);

    public virtual async Task<long> EstimateBytesAsync(CancellationToken ct = default)
    {
        var plan = await PlanAsync(ct).ConfigureAwait(false);
        return plan.EstimatedBytes;
    }

    public async Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.ProviderId != Id)
        {
            throw new ArgumentException(
                $"Plan belongs to provider '{plan.ProviderId}', not '{Id}'.", nameof(plan));
        }

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

        // §5.6 is not optional and not a separate user action: every execution is verified.
        var verification = await VerifyAsync(plan, ct).ConfigureAwait(false);

        return new CleanupResult
        {
            ProviderId = Id,
            ProviderName = Name,
            Steps = outcomes,
            Duration = stopwatch.Elapsed,
            Verification = verification,
        };
    }

    public Task<VerificationResult> VerifyAsync(CleanupPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var checks = new List<VerificationCheck>();

        foreach (var protectedPath in plan.ProtectedPaths)
        {
            ct.ThrowIfCancellationRequested();

            if (!protectedPath.ExistedBefore)
            {
                // It was never there, so its absence proves nothing. Record it so the report
                // stays honest about what was actually established.
                checks.Add(new VerificationCheck(
                    protectedPath.Path,
                    protectedPath.Reason,
                    Passed: true,
                    "Not present before the clean; nothing to preserve."));
                continue;
            }

            var survives = LongPath.FileExists(protectedPath.Path) || LongPath.DirectoryExists(protectedPath.Path);

            checks.Add(new VerificationCheck(
                protectedPath.Path,
                protectedPath.Reason,
                survives,
                survives ? "Still present." : "MISSING — it was there before the clean."));
        }

        return Task.FromResult(new VerificationResult { Checks = checks });
    }

    private async Task<StepOutcome> RunCommandAsync(RunCommandStep step, CancellationToken ct)
    {
        var before = await MeasureAllAsync(step.MeasuredPaths, ct).ConfigureAwait(false);

        var outcome = await Runner.RunAsync(step.FileName, step.Arguments, ct).ConfigureAwait(false);

        var after = await MeasureAllAsync(step.MeasuredPaths, ct).ConfigureAwait(false);
        var reclaimed = Math.Max(0, before - after);

        return new StepOutcome(step.Description, outcome.Succeeded, reclaimed, Skipped: 0, outcome.Message);
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

        // Skipped items are not a failure — see §5.3. The step only fails if the directory is
        // still there with nothing reclaimed, which means we achieved nothing at all.
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

    /// <summary>Build the warning note for §5.3, or null if nothing conflicting is running.</summary>
    protected PlanNote? BuildRunningProcessNote()
    {
        var running = Inspector.FindRunning(ConflictingProcessNames);
        if (running.Count == 0)
        {
            return null;
        }

        return new PlanNote(
            PlanNoteSeverity.Warning,
            $"{string.Join(", ", running)} {(running.Count == 1 ? "is" : "are")} running. " +
            "Anything held open will be left in place rather than removed.");
    }
}
