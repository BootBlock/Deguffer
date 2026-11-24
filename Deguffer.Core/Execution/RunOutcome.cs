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
/// <para>Derived once, because it has to appear in two places. The Storage page keeps it beside the
/// run's figures for as long as those figures stand, as a heading, its evidence and the detail, and
/// states it in the info bar as one <see cref="Statement"/> whenever there is something to answer for.
/// Formatting it at each of those surfaces is how the two came to disagree about the same run.</para>
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
/// <see cref="RunRows"/> lists it a line per location under this verdict: one paragraph for the run
/// can name neither the row nor what to do about it.</para>
/// </summary>
/// <param name="Verdict">Which of the things above happened.</param>
/// <param name="Detail">
/// What the run did, after the verdict in <see cref="Heading"/>: which providers have something to
/// answer for, and that a cancel stopped the run part-way. Empty for a run that finished and verified
/// cleanly, because the heading has then said everything.
/// </param>
/// <param name="Evidence">How many protected subjects the check covered. See <see cref="EvidenceFor"/>.</param>
/// <param name="Cancelled">
/// Whether the user cancelled the run before it finished. The verdict still stands: it covers every
/// plan, the ones never started included. See <see cref="CleanupPlanner.ExecuteAsync"/>.
/// </param>
public sealed record RunOutcome(RunVerdict Verdict, string Detail, string Evidence, bool Cancelled)
{
    /// <summary>
    /// What the safety check looks at, in words for someone who came to free space rather than to
    /// learn the tier model: the tooltip and help text behind <see cref="Heading"/>. "Protected path"
    /// means nothing to that reader, and a verdict whose subject is unexplained cannot be trusted or
    /// doubted, only ignored.
    /// </summary>
    public const string WhatIsChecked =
        "After every clean, Deguffer checks that it left alone what it was told to: each tool's own "
        + "folder, folders it does not recognise, and anything kept, by you or by its own rules.";

    /// <summary>
    /// The verdict as one plain sentence, the line the card leads with. It opens by saying whether
    /// the check passed, so the outcome reads before its explanation, and the shell sets a glyph
    /// beside it that says the same, because colour alone is not a cue (§6.5).
    ///
    /// <para>The two verdicts that are not an alarm say "incomplete" rather than "passed" or
    /// "failed": nothing proves the paths survived, which is what <see cref="VerificationResult.Passed"/>
    /// also says of them, and nothing shows this run took one.</para>
    /// </summary>
    public string Heading => HeadingFor(Verdict);

    /// <summary>The heading, then the detail: the whole account in one line, for the info bar and a report.</summary>
    public string Statement => Detail.Length == 0 ? Heading : $"{Heading} {Detail}";

    /// <summary>
    /// Whether the check passed outright, which is the one verdict that asks nothing of the reader. A
    /// page keeps what serves a fault report, such as the copy of the run's diagnostics, out of the
    /// way of a pass, where offering it suggests something went wrong.
    /// </summary>
    public bool Passed => Verdict == RunVerdict.AllSurvived;

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
    /// </summary>
    public bool NeedsReporting => Verdict != RunVerdict.AllSurvived || Cancelled;

    public static RunOutcome For(IReadOnlyList<CleanupResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var cancelled = results.Any(r => r.Interrupted);
        var evidence = EvidenceFor(results);

        // What the run did, before what §5.6 found. A cancelled run says so first, because every
        // figure beside the sentence is smaller than the preview promised for that reason.
        var did = cancelled ? "Clean cancelled part-way." : "Cleaned.";

        var failed = results.Where(r => r.Verification is { Failures.Count: > 0 }).ToList();

        if (failed.Count > 0)
        {
            // A failed run says one thing, so nothing else buries the only sentence on the screen that
            // is an alarm.
            return new RunOutcome(
                RunVerdict.VerificationFailed,
                (cancelled ? "Clean cancelled part-way, and" : "Cleaned, but")
                + $" verification failed for {Names(failed)}. "
                + "A protected path did not survive — please report this.",
                evidence,
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
                RunVerdict.RemovedFromOutside,
                $"{did} {went}. Scan again to see the machine as it is now."
                + NotChecked(results),
                evidence,
                cancelled);
        }

        if (results.Any(r => r.Verification is { Unverified.Count: > 0 }))
        {
            return new RunOutcome(
                RunVerdict.Unverified, did + NotChecked(results), evidence, cancelled);
        }

        // "Cleaned." is left off a pass: the heading has already said the run finished well, and a
        // detail of one word under it would only repeat that.
        return new RunOutcome(
            RunVerdict.AllSurvived,
            cancelled ? "Clean cancelled part-way. Anything already removed stays removed." : string.Empty,
            evidence,
            cancelled);
    }

    /// <summary>
    /// The heading for each verdict. Every verdict has a sentence of its own, and an unnamed value
    /// throws rather than falling to the pass, so a verdict added later cannot read as one by default.
    /// </summary>
    public static string HeadingFor(RunVerdict verdict) =>
        verdict switch
        {
            RunVerdict.AllSurvived =>
                "Safety check passed: everything Deguffer was told to leave alone is still there.",
            RunVerdict.Unverified =>
                "Safety check incomplete: Windows would not let Deguffer check everything it was told to leave alone.",
            RunVerdict.RemovedFromOutside =>
                "Safety check incomplete: something other than this clean removed part of what Deguffer was told to leave alone.",

            // "Reached" rather than "removed", because a path emptied or gone into is this verdict
            // as well as one deleted. See VerificationResult.Failures.
            RunVerdict.VerificationFailed =>
                "Safety check failed: this clean reached something Deguffer was told to leave alone.",
            _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
        };

    /// <summary>
    /// The count behind the verdict: how many protected subjects had to survive, and of those how
    /// many the check could look at.
    ///
    /// <para>A subject that was not there when the plan was made is left out, because it had nothing
    /// to survive, and counting it would claim more was protected than was. One Windows would not
    /// describe is counted as having to survive and not as checked, so the figure never says more
    /// was looked at than was.</para>
    /// </summary>
    private static string EvidenceFor(IReadOnlyList<CleanupResult> results)
    {
        var required = results.Sum(r => r.Verification?.MustSurvive.Count ?? 0);
        var undescribed = results.Sum(r => r.Verification?.Unverified.Count ?? 0);

        if (required == 0)
        {
            return "Nothing Deguffer was told to leave alone was there to check.";
        }

        var items = Counted.Of(required, "item", "items");

        return undescribed == 0
            ? $"Checked {items} that had to survive."
            : $"Checked {required - undescribed:N0} of {items} that had to survive.";
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
