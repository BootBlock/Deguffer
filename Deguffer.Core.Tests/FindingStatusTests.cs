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
    /// neighbouring states measure zero as well, and the "show items already clear" filter hides a
    /// row on this label alone.
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

    /// <summary>Every state has words of its own, and no two states share them.</summary>
    [Fact]
    public void EveryStateHasDistinctWordsOfItsOwn()
    {
        var labels = Enum.GetValues<FindingStatus>().Select(s => s.ToStatusLabel()).ToList();

        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
    }
}
