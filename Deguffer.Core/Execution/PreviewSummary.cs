using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// The one sentence the Storage page's info bar states once a preview has finished.
///
/// <para><em>Which</em> sentence is chosen comes from the rows' own <see cref="FindingStatus"/>
/// values and never from the byte totals, because the bar and the rows are two statements about the
/// same scan and the user reads them together. Deriving the bar independently let it announce that
/// the caches were already clear above a row saying it could not be read: every one of those states
/// measures zero, so a test on the totals cannot tell a clean machine from a location Deguffer never
/// looked at.</para>
///
/// <para>The totals are still <em>reported</em>, and this type formats them itself rather than
/// taking a finished label. Handing it one let the caller supply a figure measuring something other
/// than what the condition had just tested, which is exactly how the bar came to read "0 B can be
/// reclaimed" over rows offering gigabytes.</para>
///
/// <para>Here rather than in the view-model for the reason <see cref="ElevationOffer"/> is: a
/// sentence that has to agree with something else on screen is worth being able to hold to a
/// test.</para>
/// </summary>
public static class PreviewSummary
{
    /// <summary>
    /// What a new state gets until somebody gives it words of its own, and what a mixture of causes
    /// gets in any case. Naming two of four causes would be less true than naming none.
    /// </summary>
    private const string MixedCauses =
        "Nothing to reclaim, and not every location here is clear. Check what each row reports.";

    /// <param name="statuses">Every row of the preview, in any order.</param>
    /// <param name="selectable">
    /// Everything the user could tick across the whole preview — the ceiling
    /// <paramref name="selected"/> can reach. It is above zero whenever any row reports
    /// <see cref="FindingStatus.ReadyToClean"/>, because that status needs a step that can be ticked
    /// and measures more than nothing. The caller upholds that, which is why the branch below states
    /// the figure rather than re-testing it.
    /// </param>
    /// <param name="selected">What is currently ticked.</param>
    /// <param name="elevateLabel">
    /// What the relaunch button on the same page says — <see cref="ElevationOffer.Label"/>, so the
    /// sentence and the button it names cannot come to disagree.
    /// </param>
    public static string For(
        IEnumerable<FindingStatus> statuses,
        ScanSize selectable,
        ScanSize selected,
        string elevateLabel)
    {
        var present = new HashSet<FindingStatus>(statuses);

        if (present.Contains(FindingStatus.ReadyToClean))
        {
            // Both quantities, because the condition above tests what is available and the
            // sentence used to report only what is ticked. §3 pre-selects Tier 1 alone, so on a
            // machine whose reclaimable rows are all Tier 2 or Tier 3 nothing starts ticked, and
            // the bar read "0 B can be reclaimed" over rows offering gigabytes.
            var next = selected.Reclaimable > 0
                ? "Review the rows, then Clean."
                : "Tick the rows you want, then Clean.";

            return $"{FreeSpace.Format(selectable)} can be reclaimed, "
                + $"{FreeSpace.Format(selected)} selected. {next}";
        }

        // Real space this process may not act on is neither "ready" nor "already clear", and
        // reporting it as the latter contradicts the rows underneath, which show the size and say
        // what they need.
        if (present.Contains(FindingStatus.NeedsElevation))
        {
            return $"Nothing here can be cleared without administrator rights. Use {elevateLabel}.";
        }

        // Every row measures zero from here on, which is not the same as every row being clear.
        // Four states measure zero for a reason of their own and say so in their own words; what is
        // left after removing the two that are genuinely nothing to act on is the reason the bar
        // has to agree with.
        present.Remove(FindingStatus.AlreadyClear);
        present.Remove(FindingStatus.ToolchainMissing);

        return present.Count switch
        {
            0 => "Nothing to reclaim — these caches are already clear.",
            1 => ToCause(present.First()),
            _ => MixedCauses,
        };
    }

    /// <summary>
    /// The bar's wording for a single zero-byte cause. It states the cause rather than quoting the
    /// row's label, because the bar is summarising a set and the row is describing itself.
    /// </summary>
    private static string ToCause(FindingStatus status) => status switch
    {
        FindingStatus.UnreadableRoot =>
            "Nothing to reclaim, and Windows would not let Deguffer read some of these locations.",
        FindingStatus.NotExamined =>
            "Nothing to reclaim, and some of these locations were not examined.",
        // Deliberately "not old enough" rather than "recently changed", which the row's own label
        // has always said and this sentence used not to. Two different ages reach this state: the
        // user's guard on recently changed files, and a command that takes an age of its own —
        // FhManagew's retention, where the files held back can be a year old. Naming the guard here
        // would describe a setting the reader may never have switched on.
        FindingStatus.RecentContentHeldBack =>
            "Nothing to reclaim, and some of these hold only files that are not old enough to remove.",
        FindingStatus.AwaitingSourceFolders =>
            "Nothing to reclaim, and some of these locations need a source folder before Deguffer "
            + "can look.",
        _ => MixedCauses,
    };
}
