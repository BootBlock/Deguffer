using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Scanning;
using Deguffer.Core.SystemProtection;

namespace Deguffer.Core.Execution;

/// <summary>
/// How <see cref="PlanExecutor"/> carries out a <see cref="RemoveRestorePointsStep"/>: System Restore
/// removes each named restore point, and Windows' own storage figures, read immediately before and
/// after, say what that freed.
///
/// <para>Apart from the executor's own removals because nothing here is Deguffer's removal, for the
/// reason <see cref="DiskCleanupRun"/> is.</para>
/// </summary>
internal static class RestorePointRun
{
    public static async Task<StepOutcome> RunAsync(
        ISystemProtection protection,
        RemoveRestorePointsStep step,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // Asked again rather than trusted from the plan, which is as old as the preview: Windows makes a
        // restore point before an update or an installation, and something else can remove one.
        var listing = await Task.Run(protection.ListRestorePoints, ct).ConfigureAwait(false);

        if (listing.Answer is not ListingAnswer.Listed)
        {
            return NotRun(step, listing.Answer is ListingAnswer.NeedsElevation
                ? "System Restore lists its restore points only for an administrator."
                : listing.Failure ?? "System Restore did not list its restore points.");
        }

        var due = Due(step, listing);

        if (due.Count == 0)
        {
            return NotRun(step, "None of the restore points the preview offered is still there to remove.");
        }

        var before = await Task.Run(protection.ReadStorage, ct).ConfigureAwait(false);
        var removed = 0;
        var interrupted = false;
        List<string> failures = [];

        foreach (var point in due)
        {
            // Between restore points and never during one: System Restore's removal cannot be told to stop.
            if (ct.IsCancellationRequested)
            {
                interrupted = true;
                break;
            }

            var answer = await Task.Run(() => protection.RemoveRestorePoint(point.SequenceNumber), CancellationToken.None)
                .ConfigureAwait(false);

            if (answer is RemovalAnswer.Removed)
            {
                removed++;
            }
            else
            {
                failures.Add($"the restore point {point.Label}: {Why(answer)}");

                // An administrator's rights do not come and go between restore points.
                if (answer is RemovalAnswer.NeedsElevation)
                {
                    break;
                }
            }

            progress?.Report((removed + failures.Count) / (double)due.Count);
        }

        var after = await Task.Run(protection.ReadStorage, CancellationToken.None).ConfigureAwait(false);

        return Outcome(step, due.Count, removed, failures, interrupted, Freed(before, after));
    }

    /// <summary>
    /// The plan's restore points that are still listed, and older than the newest listed now. The second
    /// test is what keeps one restore point whatever happened since the preview: if the one the preview
    /// kept is gone, the newest of the rest stays instead.
    /// </summary>
    private static List<RestorePoint> Due(RemoveRestorePointsStep step, RestorePointListing listing)
    {
        if (listing.Newest is not { } newest)
        {
            return [];
        }

        return
        [
            .. step.Removes
                .Where(point => listing.Points.Contains(point) && point.SequenceNumber < newest.SequenceNumber)
                .OrderBy(point => point.SequenceNumber),
        ];
    }

    /// <summary>
    /// What the volumes Windows stated both times hold less afterwards, or null where it stated none both
    /// times. Paired by volume, so a figure is only ever subtracted from the same volume's: a total that
    /// lost a volume between the readings would report that volume's storage as freed. A volume Windows
    /// never states, such as one the shadow copy service does not support, is left out of both rather than
    /// stopping the count, and what it held can only make the figure smaller than the truth.
    /// </summary>
    private static long? Freed(IReadOnlyList<VolumeShadowStorage> before, IReadOnlyList<VolumeShadowStorage> after)
    {
        var paired = before
            .Where(volume => volume.Storage.Statement is Statement.Stated)
            .Join(
                after.Where(volume => volume.Storage.Statement is Statement.Stated),
                volume => volume.Volume,
                volume => volume.Volume,
                (start, end) => start.Storage.UsedBytes - end.Storage.UsedBytes,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        return paired.Count == 0 ? null : paired.Sum();
    }

    private static string Why(RemovalAnswer answer) => answer switch
    {
        RemovalAnswer.NotRemovable => "System Restore says it does not exist or cannot be removed",
        RemovalAnswer.NeedsElevation => "System Restore removes restore points only for an administrator",
        _ => "System Restore did not remove it",
    };

    private static StepOutcome Outcome(
        RemoveRestorePointsStep step,
        int due,
        int removed,
        IReadOnlyList<string> failures,
        bool interrupted,
        long? freed)
    {
        var what = removed == due
            ? $"System Restore removed {Count(removed)}."
            : $"System Restore removed {removed} of {Count(due)}.";

        if (failures.Count > 0)
        {
            what += " Not removed: " + string.Join("; ", failures) + ".";
        }

        if (interrupted)
        {
            what += " The clean was cancelled before the rest.";
        }

        // Windows' figure before less its figure after, never the plan's estimate: the estimate is a
        // ceiling that includes the restore point kept and every other shadow copy.
        if (freed is not { } bytes)
        {
            return new StepOutcome(
                step.Description,
                removed == due,
                BytesReclaimed: 0,
                Refusals.None,
                what + " Windows did not state its shadow copy storage before and after, so nothing is counted as freed.",
                Interrupted: interrupted);
        }

        return new StepOutcome(
            step.Description,
            removed == due,
            BytesReclaimed: Math.Max(0, bytes),
            Refusals.None,
            bytes < 0
                ? what + $" The shadow copy storage grew by {FreeSpace.Format(-bytes)} while they were removed."
                : what,
            Interrupted: interrupted);
    }

    private static StepOutcome NotRun(RemoveRestorePointsStep step, string why) =>
        new(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, $"Nothing was removed: {why}");

    private static string Count(int count) => count == 1 ? "1 restore point" : $"{count} restore points";
}
