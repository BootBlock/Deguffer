using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sentence the Storage page states above the rows, which has to agree with them.
///
/// The defect this covers (issue #38) is a bar that read the byte totals instead of the rows. Four
/// states measure zero without being clear, so a test on the totals cannot tell a clean machine
/// from one whose caches Deguffer was not allowed to read — and the bar announced "already clear"
/// directly above a row saying otherwise. Every case below is one of those disagreements.
/// </summary>
public sealed class PreviewSummaryTests
{
    private const string SelectedTotal = "1.5 GB";
    private const string Elevate = "Elevate and rescan";

    private static string For(params FindingStatus[] statuses) =>
        PreviewSummary.For(statuses, SelectedTotal, Elevate);

    /// <summary>
    /// The four states that measure zero and are not clear. None of them may draw the claim that
    /// the caches are already clear, whatever the totals say.
    /// </summary>
    [Theory]
    [InlineData(FindingStatus.UnreadableRoot)]
    [InlineData(FindingStatus.NotExamined)]
    [InlineData(FindingStatus.RecentContentHeldBack)]
    [InlineData(FindingStatus.AwaitingSourceFolders)]
    public void DoesNotCallTheCachesClearOverARowThatIsNotClear(FindingStatus unclear)
    {
        var summary = For(FindingStatus.AlreadyClear, unclear, FindingStatus.ToolchainMissing);

        Assert.DoesNotContain("already clear", summary, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Nothing to reclaim", summary, StringComparison.Ordinal);
    }

    /// <summary>Each of the four gets words naming its own cause when it is the only one present.</summary>
    [Theory]
    [InlineData(FindingStatus.UnreadableRoot, "would not let Deguffer read")]
    [InlineData(FindingStatus.NotExamined, "were not examined")]
    [InlineData(FindingStatus.RecentContentHeldBack, "recently changed files")]
    [InlineData(FindingStatus.AwaitingSourceFolders, "need a source folder")]
    public void NamesTheCauseWhenOnlyOneKindOfRowIsUnclear(FindingStatus unclear, string expected)
    {
        Assert.Contains(expected, For(FindingStatus.AlreadyClear, unclear), StringComparison.Ordinal);
    }

    /// <summary>
    /// Naming two causes of four would be less true than naming none, so a mixture sends the reader
    /// to the rows, which each state their own.
    /// </summary>
    [Fact]
    public void SendsTheReaderToTheRowsWhenTheCausesAreMixed()
    {
        var summary = For(FindingStatus.UnreadableRoot, FindingStatus.NotExamined);

        Assert.Contains("not every location here is clear", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("already clear", summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The claim is still made where it is true. A machine whose every row was examined and found
    /// empty, alongside tools that are not installed, has genuinely clear caches.
    /// </summary>
    [Fact]
    public void StillSaysTheCachesAreClearWhenEveryRowWasExaminedAndFoundEmpty()
    {
        Assert.Equal(
            "Nothing to reclaim — these caches are already clear.",
            For(FindingStatus.AlreadyClear, FindingStatus.ToolchainMissing, FindingStatus.AlreadyClear));
    }

    /// <summary>Space this process can act on outranks every other row, and reports the total.</summary>
    [Fact]
    public void ReportsTheSelectedTotalWhenARowCanBeCleaned()
    {
        Assert.StartsWith(
            SelectedTotal,
            For(FindingStatus.NotExamined, FindingStatus.ReadyToClean),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Real space this process may not act on is neither ready nor clear. It outranks the zero-byte
    /// causes because it is the one the user can do something about from this screen.
    /// </summary>
    [Fact]
    public void OffersElevationOverAZeroByteCause()
    {
        var summary = For(FindingStatus.UnreadableRoot, FindingStatus.NeedsElevation);

        Assert.Contains(Elevate, summary, StringComparison.Ordinal);
        Assert.DoesNotContain("already clear", summary, StringComparison.OrdinalIgnoreCase);
    }
}
