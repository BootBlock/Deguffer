using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// The figures a finished clean is reported by, each kept apart from the others (§5.4).
///
/// <para>What Deguffer measured itself removing, what sync apps were asked to release, what a program
/// marked to remove the next time it starts, and how the volume's free space actually changed are
/// four different numbers. They disagree whenever anything else writes during the run, or whenever
/// the space goes later than the run does, and presenting them as one invites distrust.</para>
/// </summary>
/// <param name="Removed">
/// What went, with the entries beside the bytes, so a clean that took only empty leftovers can say
/// what it removed rather than "0 B".
/// </param>
/// <param name="Requested">What sync apps were asked to release. See <see cref="StepOutcome.BytesRequested"/>.</param>
/// <param name="Scheduled">What a program marked to remove later. See <see cref="StepOutcome.BytesScheduled"/>.</param>
/// <param name="Checks">
/// Every §5.6 check the run has something to answer for: each one that failed, each protected path
/// something else removed, and each one Windows would not let Deguffer describe. A pass is left out,
/// and nothing else is: each of these is a path nobody could vouch for, and a request to report a
/// fault that does not say what the fault was is a dead end.
/// </param>
public sealed record RunFigures(
    ScanSize Removed,
    long Requested,
    long Scheduled,
    IReadOnlyList<VerificationCheck> Checks)
{
    public static RunFigures For(IReadOnlyList<CleanupResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var bytes = results.Sum(r => r.BytesReclaimed);

        return new RunFigures(
            new ScanSize(bytes, bytes, Entries: results.Sum(r => r.EntriesRemoved)),
            results.Sum(r => r.BytesRequested),
            results.Sum(r => r.BytesScheduled),
            [
                .. results
                    .Select(r => r.Verification)
                    .OfType<VerificationResult>()
                    .SelectMany(v => v.Failures.Concat(v.RemovedFromOutside).Concat(v.Unverified)),
            ]);
    }

    /// <summary>
    /// How the volume's free space moved across the run, or null where either reading is unknown. A
    /// figure from one reading alone would be a guess dressed as a measurement.
    /// </summary>
    public static long? FreeSpaceChange(long? before, long? after) =>
        before is { } was && after is { } now ? now - was : null;
}
