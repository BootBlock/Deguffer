namespace Deguffer.Core.Execution;

/// <summary>
/// The one sentence the Storage page's info bar states once a preview has finished.
///
/// <para>It is composed from the rows' own <see cref="FindingStatus"/> values rather than from the
/// byte totals, because the bar and the rows are two statements about the same scan and the user
/// reads them together. Deriving the bar independently let it announce that the caches were already
/// clear above a row saying it could not be read: every one of those states measures zero, so a
/// test on the totals cannot tell a clean machine from a location Deguffer never looked at.</para>
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
    /// <param name="selectedTotalLabel">What is currently ticked, already formatted.</param>
    /// <param name="elevateLabel">
    /// What the relaunch button on the same page says — <see cref="ElevationOffer.Label"/>, so the
    /// sentence and the button it names cannot come to disagree.
    /// </param>
    public static string For(
        IEnumerable<FindingStatus> statuses,
        string selectedTotalLabel,
        string elevateLabel)
    {
        var present = new HashSet<FindingStatus>(statuses);

        if (present.Contains(FindingStatus.ReadyToClean))
        {
            return $"{selectedTotalLabel} can be reclaimed. Review the rows, then Clean.";
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
        FindingStatus.RecentContentHeldBack =>
            "Nothing to reclaim, and some of these caches hold only recently changed files.",
        FindingStatus.AwaitingSourceFolders =>
            "Nothing to reclaim, and some of these locations need a source folder before Deguffer "
            + "can look.",
        _ => MixedCauses,
    };
}
