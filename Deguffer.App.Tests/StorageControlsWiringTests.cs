using Deguffer.App.ViewModels;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Storage page follows <c>StorageControls</c>: before a scan the empty card offers Scan and
/// nothing else is drawn, the bar under the list comes with the first scan, the list's own controls
/// come with its first row, and each change is told to the bindings. Which control shows in which
/// state is Core's, and proven in <c>StorageControlsTests</c> there.
/// </summary>
public sealed class StorageControlsWiringTests
{
    [Fact]
    public void BeforeAScanTheEmptyCardOffersScanAndTheBarsAreNotDrawn()
    {
        using var page = new StoragePage([Provider("temp")]);
        var controls = page.ViewModel.Controls;

        Assert.True(page.ViewModel.EmptyState.IsShown);
        Assert.Equal((false, false, true), Read(controls));
    }

    /// <summary>
    /// While the first scan runs, Cancel stands in the bar under the list, so the bar is drawn and the
    /// empty card gives Scan up. Once its first row lands, the list's controls are drawn over it, and
    /// the bindings are told each time.
    /// </summary>
    [Fact]
    public void TheFirstScanMovesScanToTheBarAndItsFirstRowBringsTheListControls()
    {
        var temp = Provider("temp");
        using var page = new StoragePage([temp]);
        var controls = page.ViewModel.Controls;
        var told = new List<(bool, bool, bool)>();
        controls.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StoragePageControls.ListControls))
            {
                told.Add(Read(controls));
            }
        };

        (bool, bool, bool)? whileScanning = null;
        temp.WhilePlanning = () => whileScanning = Read(controls);
        page.Scan();

        Assert.Equal((false, true, false), whileScanning);
        Assert.Equal((true, true, false), Read(controls));
        Assert.Equal([(false, true, false), (true, true, false), (true, true, false)], told);
    }

    /// <summary>
    /// A filter that hides every row still leaves its own box on screen to switch it back, and a
    /// rescan that empties the list to fill it again moves nothing above it.
    /// </summary>
    [Fact]
    public void TheListControlsStayOnceARowHasLandedEvenWhenNoRowIsDrawn()
    {
        var clear = new FakeCleanupProvider("clear");
        using var page = new StoragePage([clear]);
        var controls = page.ViewModel.Controls;

        page.Scan();
        Assert.Empty(page.ViewModel.Listed);
        Assert.True(page.ViewModel.EmptyState.IsShown);
        Assert.Equal((true, true, false), Read(controls));

        (bool, bool, bool)? whileRescanning = null;
        clear.WhilePlanning = () => whileRescanning = Read(controls);
        page.Scan();

        Assert.Equal((true, true, false), whileRescanning);
        Assert.Equal((true, true, false), Read(controls));
    }

    /// <summary>A first scan cancelled before any row landed leaves the page as it was before it: Scan back in the empty card.</summary>
    [Fact]
    public void AFirstScanCancelledBeforeAnyRowPutsScanBackInTheEmptyCard()
    {
        var temp = Provider("temp");
        temp.PlanFailure = new OperationCanceledException();
        using var page = new StoragePage([temp]);

        page.Scan();

        Assert.Equal((false, false, true), Read(page.ViewModel.Controls));
    }

    private static (bool ListControls, bool ActionBar, bool EmptyStateOffersScan) Read(StoragePageControls controls) =>
        (controls.ListControls, controls.ActionBar, controls.EmptyStateOffersScan);

    private static FakeCleanupProvider Provider(string id) => new(id) { Steps = [Rows.Folder(id, 10)] };
}
