using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

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
    private const string Elevate = "Elevate and rescan";

    /// <summary>4 GB available, of which 1.5 GB is ticked — the ordinary mid-selection case.</summary>
    private static readonly ScanSize Selectable = ScanSize.FromLengths(4L * 1024 * 1024 * 1024);

    private static readonly ScanSize Selected = ScanSize.FromLengths(1536L * 1024 * 1024);

    private static string For(params FindingStatus[] statuses) =>
        PreviewSummary.For(statuses, Selectable, Selected, Elevate);

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

    /// <summary>
    /// Space this process can act on outranks every other row, and reports both totals: what is
    /// there to reclaim, and how much of it the user has ticked so far.
    /// </summary>
    [Fact]
    public void ReportsBothTotalsWhenARowCanBeCleaned()
    {
        Assert.Equal(
            "4.0 GB can be reclaimed, 1.5 GB selected. Review the rows, then Clean.",
            For(FindingStatus.NotExamined, FindingStatus.ReadyToClean));
    }

    /// <summary>
    /// Issue #39. The condition above this sentence asks what is <em>available</em> and the sentence
    /// used to state what was <em>ticked</em>, so a preview whose reclaimable rows are all Tier 2 or
    /// Tier 3 — node_modules, Cargo target, Maven, the Recycle Bins — read "0 B can be reclaimed"
    /// over rows showing gigabytes. §3 pre-selects Tier 1 alone, so nothing starts ticked there.
    ///
    /// A remembered all-unticked selection and unticking every row by hand reach the same place.
    /// </summary>
    [Fact]
    public void DoesNotSayNothingCanBeReclaimedWhileRowsAreReadyToClean()
    {
        var summary = PreviewSummary.For(
            [FindingStatus.ReadyToClean], Selectable, ScanSize.Zero, Elevate);

        Assert.Equal(
            "4.0 GB can be reclaimed, 0 B selected. Tick the rows you want, then Clean.",
            summary);
    }

    /// <summary>
    /// A predicted figure keeps its qualifier in both halves of the sentence. conda's dry run
    /// reports what its own clean expects to free rather than a measurement, and
    /// <see cref="ScanSize"/> makes approximation contagious across a total — so dropping the word
    /// here would present a forecast as something counted.
    /// </summary>
    [Fact]
    public void KeepsTheQualifierOnAPredictedTotal()
    {
        var summary = PreviewSummary.For(
            [FindingStatus.ReadyToClean],
            ScanSize.Approximate(4L * 1024 * 1024 * 1024),
            ScanSize.Approximate(1536L * 1024 * 1024),
            Elevate);

        Assert.Equal(
            "about 4.0 GB can be reclaimed, about 1.5 GB selected. Review the rows, then Clean.",
            summary);
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
