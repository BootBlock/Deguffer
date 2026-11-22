using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where each row of the Storage preview is drawn. Each filter hides exactly the rows saying one
/// thing, a row waiting for a source folder goes to the folded group whatever the filters say, and a
/// row put out of sight either way can carry nothing ticked.
/// </summary>
public sealed class PreviewFilterTests
{
    private static readonly PreviewFilter Defaults = new(ShowNotInstalled: false, ShowAlreadyClear: false);

    [Fact]
    public void TheNotInstalledFilterHidesOnlyAToolThatIsNotThere()
    {
        Assert.Equal(PreviewPlace.Hidden, Defaults.Place(FindingStatus.ToolchainMissing));
        Assert.Equal(PreviewPlace.Listed, (Defaults with { ShowNotInstalled = true }).Place(FindingStatus.ToolchainMissing));
    }

    [Fact]
    public void TheAlreadyClearFilterHidesOnlyARowThatIsClear()
    {
        Assert.Equal(PreviewPlace.Hidden, Defaults.Place(FindingStatus.AlreadyClear));
        Assert.Equal(PreviewPlace.Listed, (Defaults with { ShowAlreadyClear = true }).Place(FindingStatus.AlreadyClear));
    }

    /// <summary>
    /// Each filter hides its own state and not the other's: a row has to pass both, and switching one
    /// on must not bring back what the other hides.
    /// </summary>
    [Fact]
    public void EachFilterAnswersOnlyForItsOwnState()
    {
        Assert.Equal(PreviewPlace.Hidden, new PreviewFilter(ShowNotInstalled: true, ShowAlreadyClear: false).Place(FindingStatus.AlreadyClear));
        Assert.Equal(PreviewPlace.Hidden, new PreviewFilter(ShowNotInstalled: false, ShowAlreadyClear: true).Place(FindingStatus.ToolchainMissing));
    }

    /// <summary>
    /// A row waiting for a source folder goes to the folded group under every combination of the
    /// filters. Hiding it with "not installed" would lose the row §7 names as one setting away from
    /// the largest reclaimable thing on the disk, and listing it put one row per provider saying the
    /// same thing above the rows that report something.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ARowWaitingForASourceFolderIsAlwaysFoldedIntoItsGroup(bool showNotInstalled, bool showAlreadyClear) =>
        Assert.Equal(
            PreviewPlace.AwaitingSourceFolders,
            new PreviewFilter(showNotInstalled, showAlreadyClear).Place(FindingStatus.AwaitingSourceFolders));

    /// <summary>Every other state has something to say, so no filter hides it.</summary>
    [Theory]
    [InlineData(FindingStatus.UnreadableRoot)]
    [InlineData(FindingStatus.NotExamined)]
    [InlineData(FindingStatus.RecentContentHeldBack)]
    [InlineData(FindingStatus.RefusedByWindows)]
    [InlineData(FindingStatus.OnKeepList)]
    [InlineData(FindingStatus.MailStoresHeldBack)]
    [InlineData(FindingStatus.UpdateInProgress)]
    [InlineData(FindingStatus.VirtualMachineKept)]
    [InlineData(FindingStatus.AnotherAccountKept)]
    [InlineData(FindingStatus.ReportOnly)]
    [InlineData(FindingStatus.ReadyToClean)]
    [InlineData(FindingStatus.NeedsElevation)]
    public void NoFilterHidesARowWithSomethingToSay(FindingStatus status) =>
        Assert.Equal(PreviewPlace.Listed, Defaults.Place(status));

    /// <summary>
    /// The safety half. Both filters are on by default and the folded group starts closed, so a row
    /// put out of sight either way must hold no step that can be ticked, or a selected row could be
    /// taken out of sight and still cleaned. Asserted for every kind of row the planner makes, waiting
    /// for a folder or not and present or not, across step shapes that measure zero and ones with something
    /// to remove, for both tokens. Across these, a row is out of sight exactly when nothing in it can
    /// be ticked.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARowPutOutOfSightHoldsNothingThatCanBeTicked(bool isElevated)
    {
        var empty = new DeleteDirectoryStep(@"C:\Users\testuser\AppData\Local\Fake\Cache", "Cached files");
        var shapes = new CleanupStep[][]
        {
            [],
            [empty],
            [empty with { RequiresElevation = true }],
            [empty with { Estimated = new ScanSize(4096, 0) }],
            [empty with { Estimated = ScanSize.FromLengths(4096) }],
        };

        foreach (var steps in shapes)
        {
            foreach (var (present, awaiting) in new[] { (true, false), (true, true), (false, true) })
            {
                var finding = new Finding(new FakeCleanupProvider("fake"), present, new CleanupPlan
                {
                    ProviderId = "fake",
                    ProviderName = "Fake",
                    Tier = SafetyTier.RegenerableCache,
                    WhatHappensOnNextUse = "Rebuilt.",
                    Steps = steps,
                }, awaiting);

                var listed = Defaults.Place(finding.ToStatus(isElevated)) == PreviewPlace.Listed;

                Assert.Equal(steps.Any(step => StepChoice.CanBeSelected(step, isKept: false, isElevated)), listed);
            }
        }
    }
}
