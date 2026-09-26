using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which rows of the Storage preview are drawn. Each filter hides exactly the rows saying one thing,
/// and a row it hides can carry nothing ticked.
/// </summary>
public sealed class PreviewFilterTests
{
    private static readonly PreviewFilter Defaults = new(ShowNotInstalled: false, ShowAlreadyClear: false);

    [Fact]
    public void TheNotInstalledFilterHidesOnlyAToolThatIsNotThere()
    {
        Assert.False(Defaults.Lists(FindingStatus.ToolchainMissing));
        Assert.True((Defaults with { ShowNotInstalled = true }).Lists(FindingStatus.ToolchainMissing));
    }

    [Fact]
    public void TheAlreadyClearFilterHidesOnlyARowThatIsClear()
    {
        Assert.False(Defaults.Lists(FindingStatus.AlreadyClear));
        Assert.True((Defaults with { ShowAlreadyClear = true }).Lists(FindingStatus.AlreadyClear));
    }

    /// <summary>
    /// Each filter hides its own state and not the other's: a row has to pass both, and switching one
    /// on must not bring back what the other hides.
    /// </summary>
    [Fact]
    public void EachFilterAnswersOnlyForItsOwnState()
    {
        Assert.False(new PreviewFilter(ShowNotInstalled: true, ShowAlreadyClear: false).Lists(FindingStatus.AlreadyClear));
        Assert.False(new PreviewFilter(ShowNotInstalled: false, ShowAlreadyClear: true).Lists(FindingStatus.ToolchainMissing));
    }

    /// <summary>
    /// Every other state has something to say, so no filter hides it. The row waiting for a folder is
    /// the one §7 names: absent like a missing tool, and the one row the user can act on.
    /// </summary>
    [Theory]
    [InlineData(FindingStatus.AwaitingSourceFolders)]
    [InlineData(FindingStatus.UnreadableRoot)]
    [InlineData(FindingStatus.NotExamined)]
    [InlineData(FindingStatus.RecentContentHeldBack)]
    [InlineData(FindingStatus.RefusedByWindows)]
    [InlineData(FindingStatus.OnKeepList)]
    [InlineData(FindingStatus.MailStoresHeldBack)]
    [InlineData(FindingStatus.UpdateInProgress)]
    [InlineData(FindingStatus.ReadyToClean)]
    [InlineData(FindingStatus.NeedsElevation)]
    public void NoFilterHidesARowWithSomethingToSay(FindingStatus status) =>
        Assert.True(Defaults.Lists(status));

    /// <summary>
    /// The safety half. Both filters are on by default, so a row they hide must hold no step that can
    /// be ticked, or a selected row could be taken out of sight and still cleaned. Asserted across
    /// every step shape that measures zero, for both tokens.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARowTheFiltersHideHoldsNothingThatCanBeTicked(bool isElevated)
    {
        var empty = new DeleteDirectoryStep(@"C:\Users\testuser\AppData\Local\Fake\Cache", "Cached files");
        var shapes = new CleanupStep[][]
        {
            [],
            [empty],
            [empty with { RequiresElevation = true }],
            [empty with { Estimated = new ScanSize(4096, 0) }],
        };

        foreach (var steps in shapes)
        {
            var finding = new Finding(new FakeCleanupProvider("fake"), IsPresent: true, new CleanupPlan
            {
                ProviderId = "fake",
                ProviderName = "Fake",
                Tier = SafetyTier.RegenerableCache,
                WhatHappensOnNextUse = "Rebuilt.",
                Steps = steps,
            });

            var status = finding.ToStatus(isElevated);

            Assert.Equal(FindingStatus.AlreadyClear, status);
            Assert.False(Defaults.Lists(status));
            Assert.DoesNotContain(steps, step => StepChoice.CanBeSelected(step, isKept: false, isElevated));
        }
    }
}
