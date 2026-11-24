using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>How a finished run's §5.6 verification came out, from the clean result to the alarm.</summary>
public enum RunVerdict
{
    /// <summary>Every protected path was still there.</summary>
    AllSurvived,

    /// <summary>
    /// Nothing went missing, and Windows would not describe at least one protected path after the
    /// run, so it could not be checked. Worth saying, and not an alarm — see
    /// <see cref="VerificationOutcome.Unverified"/>.
    /// </summary>
    Unverified,

    /// <summary>
    /// A protected path went missing, and this run demonstrably did not take it. Worth saying, and
    /// not an alarm — see <see cref="VerificationOutcome.RemovedFromOutside"/>.
    /// </summary>
    RemovedFromOutside,

    /// <summary>A protected path went missing where this run could have taken it.</summary>
    VerificationFailed,
}

/// <summary>
/// What a finished clean says about itself: the §5.6 verdict.
///
/// <para>One sentence, derived once, because it has to appear in two places. The Storage page keeps
/// it beside the run's figures for as long as those figures stand, and states it in the info bar as
/// well whenever there is something to answer for. Formatting it at each of those surfaces is how
/// the two came to disagree about the same run.</para>
///
/// <para>Here rather than in the view-model for the reason <see cref="PreviewSummary"/> is: §5.6 is
/// reported, not just performed, so the words that report it are worth being able to hold to a
/// test.</para>
///
/// <para>It does not restate what was reclaimed. The page carries that figure separately under a
/// label of its own (§5.4), and a sentence repeating it beside that label would be one more thing
/// able to contradict it.</para>
///
/// <para>Nor does it say what the run left behind. That belongs to the location that left it, and
/// <see cref="RunRows"/> lists it a line per location under this sentence: one paragraph for the run
/// can name neither the row nor what to do about it.</para>
/// </summary>
/// <param name="Statement">The sentence, always non-empty: a run that verified cleanly still says so.</param>
/// <param name="Verdict">Which of the things above happened.</param>
/// <param name="Cancelled">
/// Whether the user cancelled the run before it finished. The verdict still stands: it covers every
/// plan, the ones never started included. See <see cref="CleanupPlanner.ExecuteAsync"/>.
/// </param>
public sealed record RunOutcome(string Statement, RunVerdict Verdict, bool Cancelled)
{
    /// <summary>
    /// Whether a rule was over-broad. The headline, and the only verdict that is an alarm: the user
    /// needs to know before the next run.
    /// </summary>
    public bool VerificationFailed => Verdict == RunVerdict.VerificationFailed;

    /// <summary>
    /// Whether this sentence has to hold the info bar rather than yield to the fresh preview's
    /// totals. Every verdict but the clean one does: one is a fault to report, one is the reason the
    /// run's figures describe a machine that moved underneath them, and one names paths nobody could
    /// check, which the fresh preview's totals would not mention. A cancelled run does too, because
    /// the totals would not say that the clean stopped part-way.
    ///
    /// <para>The Storage page opens the run's details for the same reasons, so the paths behind the
    /// sentence and the rows a cancel cut short are in view without a click.</para>
    /// </summary>
    public bool NeedsReporting => Verdict != RunVerdict.AllSurvived || Cancelled;

    public static RunOutcome For(IReadOnlyList<CleanupResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var cancelled = results.Any(r => r.Interrupted);

        // What the run did, before what §5.6 found. A cancelled run says so first, because every
        // figure beside the sentence is smaller than the preview promised for that reason.
        var did = cancelled ? "Clean cancelled part-way." : "Cleaned.";

        var failed = results.Where(r => r.Verification is { Failures.Count: > 0 }).ToList();

        if (failed.Count > 0)
        {
            // A failed run says one thing, so nothing else buries the only sentence on the screen that
            // is an alarm.
            return new RunOutcome(
                (cancelled ? "Clean cancelled part-way, and" : "Cleaned, but")
                + $" verification failed for {Names(failed)}. "
                + "A protected path did not survive — please report this.",
                RunVerdict.VerificationFailed,
                cancelled);
        }

        var outside = results.Where(r => r.Verification is { RemovedFromOutside.Count: > 0 }).ToList();

        if (outside.Count > 0)
        {
            var count = outside.Sum(r => r.Verification!.RemovedFromOutside.Count);

            // Both grammatical forms written out, for the reason LiveTreeVeto's note records: a
            // sentence that reads correctly only on a machine with more than one of something is
            // what driving the real window is for.
            //
            // Nothing about the folder holding the path, and nothing about when it went. A path whose
            // folder went too, and one a running program removed from a folder the run emptied around
            // it, are both this verdict, and the second can go while the clean runs. See PlanVerifier.
            var went = Counted.Form(
                count,
                $"One protected path for {Names(outside)} went missing after the scan, and "
                + "nothing in this run took it",
                $"{count:N0} protected paths for {Names(outside)} went missing after the scan, and "
                + "nothing in this run took them");

            return new RunOutcome(
                $"{did} {went}. Scan again to see the machine as it is now."
                + NotChecked(results),
                RunVerdict.RemovedFromOutside,
                cancelled);
        }

        if (results.Any(r => r.Verification is { Unverified.Count: > 0 }))
        {
            return new RunOutcome(did + NotChecked(results), RunVerdict.Unverified, cancelled);
        }

        return new RunOutcome(
            (cancelled
                ? "Clean cancelled part-way. Anything already removed stays removed, and all protected paths survived."
                : "All protected paths survived."),
            RunVerdict.AllSurvived,
            cancelled);
    }

    /// <summary>
    /// The protected paths Windows would not describe after the run, or nothing where there were none.
    ///
    /// <para>The cause is named because the reader cannot guess it and it is not Deguffer's to
    /// change: an access rule, or a directory link Windows declines to follow. Relocating a cache
    /// onto another drive with a link is something developers do on purpose, so a sentence that read
    /// as a fault would be wrong about the ordinary case.</para>
    /// </summary>
    private static string NotChecked(IReadOnlyList<CleanupResult> results)
    {
        var unverified = results.Where(r => r.Verification is { Unverified.Count: > 0 }).ToList();
        var count = unverified.Sum(r => r.Verification!.Unverified.Count);

        if (count == 0)
        {
            return string.Empty;
        }

        var paths = Counted.Form(
            count,
            $" Windows would not describe one protected path for {Names(unverified)} after the clean, "
            + "so Deguffer could not check that it survived.",
            $" Windows would not describe {count:N0} protected paths for {Names(unverified)} after the "
            + "clean, so Deguffer could not check that they survived.");

        return paths + " An access rule, or a link Windows declines to follow, is the usual cause.";
    }

    /// <summary>Each provider that has something to answer for, because each one is a separate rule.</summary>
    private static string Names(IReadOnlyList<CleanupResult> results) =>
        string.Join(", ", results.Select(r => r.ProviderName));
}
