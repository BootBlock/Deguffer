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
    /// totals. Every verdict but the clean one does: one is a fault to report, one is the reason the
    /// run's figures describe a machine that moved underneath them, and one names paths nobody could
    /// check, which the fresh preview's totals would not mention.
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
                ? $"One protected path for {Names(outside)} went missing between the scan and "
                  + "the clean, along with the folder holding it"
                : $"{count} protected paths for {Names(outside)} went missing between the scan "
                  + "and the clean, along with the folders holding them";

            // What the run left behind stays on this one, because it is not an alarm and because
            // both facts explain the same thing: why the figures are not what the preview implied.
            return new RunOutcome(
                $"Cleaned. {went} — which no step in this run named. Scan again to see the "
                + "machine as it is now."
                + NotChecked(results)
                + LeftBehind(results),
                RunVerdict.RemovedFromOutside);
        }

        if (results.Any(r => r.Verification is { Unverified.Count: > 0 }))
        {
            return new RunOutcome("Cleaned." + NotChecked(results) + LeftBehind(results), RunVerdict.Unverified);
        }

        return new RunOutcome("All protected paths survived." + LeftBehind(results), RunVerdict.AllSurvived);
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

        var paths = count == 1
            ? $" Windows would not describe one protected path for {Names(unverified)} after the clean, "
              + "so Deguffer could not check that it survived."
            : $" Windows would not describe {count} protected paths for {Names(unverified)} after the "
              + "clean, so Deguffer could not check that they survived.";

        return paths + " An access rule, or a link Windows declines to follow, is the usual cause.";
    }

    /// <summary>Each provider that has something to answer for, because each one is a separate rule.</summary>
    private static string Names(IReadOnlyList<CleanupResult> results) =>
        string.Join(", ", results.Select(r => r.ProviderName));

    /// <summary>
    /// What the run did not remove, or nothing where it removed everything it named.
    ///
    /// <para>The causes are reported beside each other and never folded together. Another program
    /// holding files open is answered by closing it. Windows refusing for any other reason is not
    /// answered by waiting, and it is stated with its size, because a count of files reads as a
    /// handful of locked ones whatever it holds — "252994 item(s) in use were left alone" was the
    /// whole account of 5.9 GB a clean could not take, and the next preview offered it again. One
    /// more cause is Deguffer honouring the setting the user chose, and the last is Deguffer declining
    /// to touch an entry it found a program working in.</para>
    ///
    /// <para>What the next preview does with a refusal is said as well, because it is the answer to
    /// the question a refusal raises: will this row keep offering what it cannot take?</para>
    ///
    /// <para>Folders Windows refused come after that promise, never before it. The promise is about
    /// bytes the next preview leaves out of its figures, and a folder holds none, so a folder sentence
    /// ahead of it would read as covered by it.</para>
    /// </summary>
    private static string LeftBehind(IReadOnlyList<CleanupResult> results)
    {
        var refused = results.Aggregate(Refusals.None, (total, result) => total + result.Refused);
        var folders = results.Aggregate(FolderRefusals.None, (total, result) => total + result.RefusedFolders);
        var kept = results.Sum(r => r.KeptCount);
        var spared = results.Sum(r => r.SparedCount);
        var stores = results.Sum(r => r.MailStoreCount);

        return (refused.InUse.Files > 0
                ? $" Another program had {refused.InUse.Files:N0} file(s) "
                  + $"({FreeSpace.Format(refused.InUse.Bytes)}) open, so they were left in place."
                : string.Empty)
            + (refused.Denied.Files > 0
                ? $" Windows would not let Deguffer remove {refused.Denied.Files:N0} file(s) "
                  + $"({FreeSpace.Format(refused.Denied.Bytes)})."
                : string.Empty)
            // Qualified, because one refusal is invisible to the preview's question: a running
            // program's own files open for deletion and refuse only the deletion. See DeletionProbe.
            + (refused.IsEmpty
                ? string.Empty
                : " The next scan leaves out whatever is still refused, apart from a running program's own files.")
            + (folders.InUse > 0
                ? $" Another program was using {folders.InUse:N0} folder(s), so they were left in place."
                : string.Empty)
            + (folders.Denied > 0
                ? $" Windows would not let Deguffer remove {folders.Denied:N0} folder(s)."
                : string.Empty)
            + (kept > 0 ? $" {kept} file(s) changed too recently to remove." : string.Empty)

            // Worded away from the first clause on purpose. Both are about something being in use,
            // and side by side they read as one sentence printed twice — but one is Windows refusing
            // to release a handle and the other is Deguffer declining to enter a folder it found a
            // program working in, and only the second names a folder the user chose to keep.
            + (spared > 0 ? $" {spared} folder(s) a running program is working in were kept." : string.Empty)

            // Last, and with its reason, because it is the one cause that asks nothing of the reader:
            // no program to close, no setting to change and nothing to wait for (§9).
            + (stores > 0
                ? $" {stores} Outlook data file(s) were left where they were, because Deguffer never removes one."
                : string.Empty);
    }
}
