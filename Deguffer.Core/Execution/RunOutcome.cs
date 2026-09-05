namespace Deguffer.Core.Execution;

/// <summary>How a finished run's §5.6 verification came out, worst first.</summary>
public enum RunVerdict
{
    /// <summary>Every protected path was still there.</summary>
    AllSurvived,

    /// <summary>
    /// A protected path went missing, and this run demonstrably did not take it. Worth saying, and
    /// not an alarm — see <see cref="VerificationOutcome.RemovedFromOutside"/>.
    /// </summary>
    RemovedFromOutside,

    /// <summary>A protected path went missing where this run could have taken it.</summary>
    VerificationFailed,
}

/// <summary>
/// What a finished clean says about itself: the §5.6 verdict, and whatever the run left behind.
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
/// </summary>
/// <param name="Statement">The sentence, always non-empty: a run that verified cleanly still says so.</param>
/// <param name="Verdict">Which of the three things above happened.</param>
public sealed record RunOutcome(string Statement, RunVerdict Verdict)
{
    /// <summary>
    /// Whether a rule was over-broad. The headline, and the only verdict that is an alarm: the user
    /// needs to know before the next run.
    /// </summary>
    public bool VerificationFailed => Verdict == RunVerdict.VerificationFailed;

    /// <summary>
    /// Whether this sentence has to hold the info bar rather than yield to the fresh preview's
    /// totals. Both of the non-clean verdicts do: one is a fault to report, and the other is the
    /// reason the run's figures describe a machine that moved underneath them.
    /// </summary>
    public bool NeedsReporting => Verdict != RunVerdict.AllSurvived;

    public static RunOutcome For(IReadOnlyList<CleanupResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var failed = results.Where(r => r.Verification is { Failures.Count: > 0 }).ToList();

        if (failed.Count > 0)
        {
            // A failed run says one thing. What the run left behind is routine, and appending it
            // here would bury the only sentence on the screen that is an alarm.
            return new RunOutcome(
                $"Cleaned, but verification failed for {Names(failed)}. " +
                "A protected path did not survive — please report this.",
                RunVerdict.VerificationFailed);
        }

        var outside = results.Where(r => r.Verification is { RemovedFromOutside.Count: > 0 }).ToList();

        if (outside.Count > 0)
        {
            var count = outside.Sum(r => r.Verification!.RemovedFromOutside.Count);

            // Both grammatical forms written out, for the reason LiveTreeVeto's note records: a
            // sentence that reads correctly only on a machine with more than one of something is
            // what driving the real window is for. The "(s)" shorthand the counts below use does
            // not stretch to a clause that also has to agree in "it" and "them".
            var went = count == 1
                ? $"One protected path for {Names(outside)} went missing between the preview and "
                  + "the clean, along with the folder holding it"
                : $"{count} protected paths for {Names(outside)} went missing between the preview "
                  + "and the clean, along with the folders holding them";

            // What the run left behind stays on this one, because it is not an alarm and because
            // both facts explain the same thing: why the figures are not what the preview implied.
            return new RunOutcome(
                $"Cleaned. {went} — which no step in this run named. Preview again to see the "
                + "machine as it is now."
                + LeftBehind(results),
                RunVerdict.RemovedFromOutside);
        }

        return new RunOutcome("All protected paths survived." + LeftBehind(results), RunVerdict.AllSurvived);
    }

    /// <summary>Each provider that has something to answer for, because each one is a separate rule.</summary>
    private static string Names(IReadOnlyList<CleanupResult> results) =>
        string.Join(", ", results.Select(r => r.ProviderName));

    /// <summary>
    /// What the run did not remove, or nothing where it removed everything it named.
    ///
    /// The two counts are reported beside each other and never folded together. One is Windows
    /// refusing, which the user can act on by closing something; the other is Deguffer honouring the
    /// setting they chose. Saying nothing about the second leaves a run that reclaimed less than the
    /// preview implied with no stated reason on screen at all.
    /// </summary>
    private static string LeftBehind(IReadOnlyList<CleanupResult> results)
    {
        var skipped = results.Sum(r => r.SkippedCount);
        var kept = results.Sum(r => r.KeptCount);

        return (skipped > 0 ? $" {skipped} item(s) in use were left alone." : string.Empty)
            + (kept > 0 ? $" {kept} file(s) changed too recently to remove." : string.Empty);
    }
}
