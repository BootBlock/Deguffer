using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// The update hold a clean asks immediately before a step that is
/// <see cref="CleanupStep.HeldWhileUpdating"/>: whether Windows is now in the middle of an update, read
/// from the machine itself and never from the preview's <see cref="PlanningServicing"/>.
///
/// <para><b>Deguffer's own servicing session is waited out, not counted.</b> Windows keeps the
/// servicing stack a DISM call woke running for minutes after the call returns, and a preview runs the
/// component store's analysis seconds before the clean. A step held by that stack could not run
/// straight after a preview, and it cannot be told from a stack an update reuses, so a step that is not
/// itself a DISM command waits for it to stop and is then asked like any other. A DISM command is
/// serialised against an update by Windows, so the stack never held it (see
/// <see cref="RunCommandStep.ServicingCalls"/>).</para>
/// </summary>
/// <param name="servicing">The machine's servicing, whose restart keys are read afresh each time.</param>
/// <param name="inspector">
/// The process table, read again where its snapshot is past <see cref="IUseCheck.ReadingLimit"/>.
/// </param>
/// <param name="time">What the wait for Deguffer's own servicing session is timed and paced on.</param>
internal sealed class UpdateHoldAtClean(IWindowsServicing servicing, IProcessInspector inspector, TimeProvider time)
{
    /// <summary>
    /// How long a step held only by Deguffer's own servicing session waits for it to stop. The stack a
    /// component store analysis woke stopped about two minutes after the analysis on Windows 11 24H2.
    /// Well past that, the stack is more likely doing something else, and the step is held as for an
    /// update.
    /// </summary>
    internal static readonly TimeSpan OwnServicingWait = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan OwnServicingPoll = TimeSpan.FromSeconds(2);

    /// <summary>The record every provider built on <paramref name="servicing"/> shares.</summary>
    private readonly OwnServicingCalls _ownServicing = OwnServicingCalls.For(servicing);

    /// <summary>
    /// Why <paramref name="step"/> must not run because Windows is now in the middle of an update, or
    /// null where nothing says it is.
    /// </summary>
    public async Task<string?> WhyHeldAsync(CleanupStep step, CancellationToken ct)
    {
        var ownCalls = step.ServicingRecord;
        var state = Read();

        if (ownCalls is null)
        {
            var waiting = time.GetTimestamp();

            while (UnfinishedUpdate.OnlyOwnServicing(state, _ownServicing))
            {
                if (time.GetElapsedTime(waiting) >= OwnServicingWait)
                {
                    return UnfinishedUpdate.OwnServicingOutlasted(OwnServicingWait);
                }

                await Task.Delay(OwnServicingPoll, time, ct).ConfigureAwait(false);
                state = Read();
            }
        }

        if (UnfinishedUpdate.HoldsEverything(state, ownCalls) is { } everything)
        {
            return $"Nothing was removed: {everything}";
        }

        // What the step is about, which for a removal is every folder it destroys: a restart due to
        // change something inside one means the update that wrote it has not finished.
        return step.Subjects.FirstOrDefault(servicing.HasPendingOperationsIn) is { } pending
            ? UnfinishedUpdate.PendingNow(pending)
            : null;
    }

    /// <summary>The machine's servicing as it is now, within <see cref="IUseCheck.ReadingLimit"/>.</summary>
    private ServicingState Read()
    {
        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

        return ServicingState.Read(servicing, inspector);
    }
}
