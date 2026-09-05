namespace Deguffer.Core.Execution;

/// <summary>
/// What a finished clean says about itself: the §5.6 verdict, and whatever the run left behind.
///
/// <para>One sentence, derived once, because it has to appear in two places. The Storage page keeps
/// it beside the run's figures for as long as those figures stand, and states it in the info bar as
/// well whenever verification failed. Formatting it at each of those surfaces is how the two came to
/// disagree about the same run.</para>
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
/// <param name="VerificationFailed">
/// Whether a protected path did not survive. The headline: it means a rule was over-broad, and the
/// user needs to know before the next run.
/// </param>
public sealed record RunOutcome(string Statement, bool VerificationFailed)
{
    public static RunOutcome For(IReadOnlyList<CleanupResult> results)
    {
        var failed = results.Where(r => r.Verification is { Passed: false }).ToList();

        if (failed.Count > 0)
        {
            return new RunOutcome(
                $"Cleaned, but verification failed for {string.Join(", ", failed.Select(f => f.ProviderName))}. " +
                "A protected path did not survive — please report this.",
                VerificationFailed: true);
        }

        var skipped = results.Sum(r => r.SkippedCount);

        // Reported beside the skipped count and never folded into it. One is Windows refusing, which
        // the user can act on by closing something; the other is Deguffer honouring the setting they
        // chose. Saying nothing about the second leaves a run that reclaimed less than the preview
        // implied with no stated reason on screen at all.
        var kept = results.Sum(r => r.KeptCount);

        return new RunOutcome(
            "All protected paths survived."
            + (skipped > 0 ? $" {skipped} item(s) in use were left alone." : string.Empty)
            + (kept > 0 ? $" {kept} file(s) changed too recently to remove." : string.Empty),
            VerificationFailed: false);
    }
}
