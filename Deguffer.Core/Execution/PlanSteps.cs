using System.Runtime.ExceptionServices;

namespace Deguffer.Core.Execution;

/// <summary>
/// Runs one plan's steps in the plan's order, starting each as soon as <see cref="StepOverlap"/> lets it
/// run beside the steps still under way, up to <see cref="MostAtOnce"/> of them.
///
/// <para><b>Started in order, never past a step that must wait.</b> A step that may not run beside
/// those under way holds back every step after it until it can start, so a step nested inside an
/// earlier one starts only once that one has ended, and a step that runs alone starts once every step
/// before it has ended and ends before any step after it starts.</para>
///
/// <para><b>Reported in order.</b> The steps started are always the first so many of the plan, and
/// their outcomes are handed back in the plan's order whatever order they ended in.</para>
///
/// <para><b>Cancellation stops new steps from starting.</b> Each step under way is left to stop as it
/// does when it runs alone, which reports it as stopped with what it had done by then, and is waited
/// for, so §5.6 is answered for everything that started. So is a step that reports itself stopped: no
/// step starts after it.</para>
///
/// <para><b>A step that throws stops the plan</b> as it does when it runs alone. The steps beside it
/// are cancelled and waited for, because a removal still running when the exception reached the
/// caller would be acting on the disk with nobody left to say what it did, and the first exception is
/// then thrown unchanged.</para>
/// </summary>
internal static class PlanSteps
{
    /// <summary>
    /// The most steps of one plan under way at once. What a small step costs besides its deletions is
    /// waits on Windows rather than work for a processor: a reading of the process table, the question
    /// of where its volume is mounted, a few probes and the walk's first listings. Several of those can
    /// overlap whatever the processor count, and the deletions themselves share one bound for the plan
    /// (<see cref="Scanning.ThreadAllowance"/>), so this bounds the questions in flight rather than the
    /// disk's queue.
    /// </summary>
    public const int MostAtOnce = 8;

    /// <param name="run">
    /// Carries out the step at its index and says what happened, reporting its own 0 to 1 to the
    /// progress it is handed and stopping when the token it is handed is cancelled.
    /// </param>
    /// <param name="progress">The plan's own 0 to 1, or null where nothing is reported.</param>
    /// <param name="ct">Stops new steps from starting, and is passed to each step under way.</param>
    public static async Task<PlanStepsRun> RunAsync(
        IReadOnlyList<CleanupStep> steps,
        StepOverlap overlap,
        Func<int, IProgress<double>?, CancellationToken, Task<StepOutcome>> run,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var bar = progress is null ? null : new StepsProgress(progress, ProgressWeights.For(steps.Select(s => s.EstimatedBytes)));
        var outcomes = new StepOutcome[steps.Count];
        var underway = new Dictionary<Task<StepOutcome>, int>();
        var next = 0;
        var stopping = false;
        var interrupted = false;
        ExceptionDispatchInfo? failure = null;

        while (true)
        {
            while (!stopping && next < steps.Count && underway.Count < MostAtOnce)
            {
                if (ct.IsCancellationRequested)
                {
                    interrupted = stopping = true;
                    break;
                }

                if (!overlap.MayStartBeside(next, underway.Values))
                {
                    break;
                }

                var index = next++;

                // On a thread of its own from the start, because what a step asks before its first wait
                // is the fixed cost the steps beside it are meant to overlap.
                underway.Add(Task.Run(() => run(index, StepsProgress.Of(bar, index), stop.Token), CancellationToken.None), index);
            }

            if (underway.Count == 0)
            {
                break;
            }

            var ended = await Task.WhenAny(underway.Keys).ConfigureAwait(false);
            var step = underway[ended];
            underway.Remove(ended);

            try
            {
                outcomes[step] = await ended.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Kept, not thrown here: the steps beside it are still acting on the disk. Only the
                // first is kept, as WorkerCrew keeps a helper's, because a step that throws after it was
                // cancelled by this one has failed because of it.
                if (failure is null)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                    stopping = true;
                    await stop.CancelAsync().ConfigureAwait(false);
                }

                continue;
            }

            // A stopped step's weight is not added, so the bar stays where the step left it.
            if (outcomes[step].Interrupted)
            {
                interrupted = stopping = true;
            }
            else
            {
                bar?.Finished(step);
            }
        }

        failure?.Throw();

        return new PlanStepsRun(outcomes[..next], interrupted);
    }
}

/// <summary>What <see cref="PlanSteps.RunAsync"/> did: the outcome of each step it started, in the plan's order.</summary>
/// <param name="Interrupted">Whether the clean was cancelled before every step had run to its end.</param>
internal sealed record PlanStepsRun(IReadOnlyList<StepOutcome> Outcomes, bool Interrupted);
