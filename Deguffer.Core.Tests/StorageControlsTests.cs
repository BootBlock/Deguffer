using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Before a scan the Storage page shows the scan and nothing else. The list's own controls come with
/// the first row and stay, and the page always offers exactly one Scan.
/// </summary>
public sealed class StorageControlsTests
{
    [Fact]
    public void BeforeAnyScanOnlyTheEmptyCardOffersAnything()
    {
        var controls = StorageControls.For(rowsProduced: false, busy: false);

        Assert.False(controls.ListControls);
        Assert.False(controls.ActionBar);
        Assert.True(controls.EmptyStateOffersScan);
    }

    /// <summary>Cancel stands in Scan's place in the bar under the list, so the bar comes as soon as the first scan starts.</summary>
    [Fact]
    public void AFirstScanStillWithoutRowsMovesScanToTheBarAndLeavesTheListControlsHidden()
    {
        var controls = StorageControls.For(rowsProduced: false, busy: true);

        Assert.False(controls.ListControls);
        Assert.True(controls.ActionBar);
        Assert.False(controls.EmptyStateOffersScan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnceAScanHasProducedARowEveryControlIsDrawnWhetherOrNotWorkRuns(bool busy)
    {
        var controls = StorageControls.For(rowsProduced: true, busy);

        Assert.True(controls.ListControls);
        Assert.True(controls.ActionBar);
        Assert.False(controls.EmptyStateOffersScan);
    }
}
