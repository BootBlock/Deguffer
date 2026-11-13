using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Which of a plan's file steps a guard keeps, the steps whose whole subject is one recent file.
///
/// <para><b>On the measurement's word where it can be taken.</b> The scanner judged each file it
/// measured by the file's own times, from the read that sized it, and the step carries that verdict
/// (<see cref="DeleteFileStep.AgeVerdict"/>), so the file is not asked the same question twice.</para>
///
/// <para><b>The file is read where the verdict cannot answer</b>: a step built without one, and a
/// verdict given under a guard other than <paramref name="keep"/>, such as the user's guard where a
/// provider's own floor is stricter. Those reads are made several at once through
/// <see cref="DiskBoundWork"/>.</para>
///
/// <para>Either answer is the one <see cref="MinimumAge.ProtectsFile"/> gives, by the file's own times
/// and a link's own for a link, so what the guard keeps does not change. The removal asks the guard
/// again of every file it deletes, so a file written after the plan was made is still left.</para>
/// </summary>
internal static class RecentFileSteps
{
    /// <summary>The steps among <paramref name="steps"/> that <paramref name="keep"/> keeps, in their order.</summary>
    /// <param name="tuning">What the walks of each volume run with, which bounds the reads made here.</param>
    public static async Task<IReadOnlyList<DeleteFileStep>> KeptAsync(
        IReadOnlyList<DeleteFileStep> steps,
        MinimumAge keep,
        ScanTuner tuning,
        CancellationToken ct)
    {
        if (!keep.IsOn)
        {
            return [];
        }

        var unjudged = steps.Where(step => step.AgeVerdict?.Under(keep) is null).Select(step => step.Path).ToList();

        var read = await DiskBoundWork.EachAsync(unjudged, tuning, keep.ProtectsFile, ct).ConfigureAwait(false);

        var kept = new List<DeleteFileStep>();
        var next = 0;

        foreach (var step in steps)
        {
            if (step.AgeVerdict?.Under(keep) ?? read[next++])
            {
                kept.Add(step);
            }
        }

        return kept;
    }
}
