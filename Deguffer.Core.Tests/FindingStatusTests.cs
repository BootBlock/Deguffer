using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The words a preview row states beside its size.
///
/// They live with the status value rather than in the view-model so that the row and the sentence
/// above it are read off one answer — see <see cref="PreviewSummaryTests"/> for what went wrong
/// while they were two.
/// </summary>
public sealed class FindingStatusTests
{
    /// <summary>
    /// The dangerous direction: exactly one state may claim the folder is clear. The three
    /// neighbouring states measure zero as well, and telling the user a folder is clear when
    /// Deguffer never read it is the whole of issue #38.
    /// </summary>
    [Theory]
    [InlineData(FindingStatus.AwaitingSourceFolders)]
    [InlineData(FindingStatus.ToolchainMissing)]
    [InlineData(FindingStatus.UnreadableRoot)]
    [InlineData(FindingStatus.NotExamined)]
    [InlineData(FindingStatus.RecentContentHeldBack)]
    [InlineData(FindingStatus.ReadyToClean)]
    [InlineData(FindingStatus.NeedsElevation)]
    public void OnlyTheClearStateSaysAlreadyClear(FindingStatus status)
    {
        Assert.NotEqual("Already clear", status.ToStatusLabel());
    }

    [Fact]
    public void TheClearStateSaysAlreadyClear()
    {
        Assert.Equal("Already clear", FindingStatus.AlreadyClear.ToStatusLabel());
    }

    /// <summary>
    /// The label is drawn under the size, in the one column the standard row pins, and every other
    /// thing on that row is positioned against that column's left edge. So a label wider than the
    /// column does not merely look cramped: it widens the column on its own row, and the "What is
    /// this?" link on that row alone stops lining up with the rest of the list.
    ///
    /// <para>The column is 104 effective pixels. The row's secondary type measures about 5.3 of
    /// those per character at its widest — "Ready to clean" renders 74 pixels wide and "Not
    /// installed" 65 — which puts the ceiling at twenty characters. Two labels were over it and
    /// pushed their rows' links visibly left.</para>
    /// </summary>
    [Fact]
    public void NoStateSaysMoreThanTheRowsPinnedColumnHolds()
    {
        const int ceiling = 20;

        Assert.All(
            Enum.GetValues<FindingStatus>(),
            status => Assert.InRange(status.ToStatusLabel().Length, 1, ceiling));
    }

    /// <summary>Every state has words of its own, and no two states share them.</summary>
    [Fact]
    public void EveryStateHasDistinctWordsOfItsOwn()
    {
        var labels = Enum.GetValues<FindingStatus>().Select(s => s.ToStatusLabel()).ToList();

        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
    }
}
