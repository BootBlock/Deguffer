using System.Collections.Specialized;
using Deguffer.Testing;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// The Storage page's scan: rows built one provider at a time inside progress callbacks, placed and
/// filtered as they land, and the page's figures following them, including through a scan that
/// fails or is cancelled part of the way.
/// </summary>
public class CleanViewModelScanTests
{
    /// <summary>
    /// A row whose steps start ticked is built inside a progress callback, where an exception reaches
    /// nobody. When building one threw, the row silently never appeared, for every provider §3
    /// pre-selects.
    /// </summary>
    [Fact]
    public void APreSelectedRowAppearsTickedAndIsTotalled()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("a", 1024), page.Cache("b", 1024)];

        page.Scan();

        var row = Assert.Single(page.ViewModel.Findings);
        Assert.True(row.IsSelected);
        Assert.Equal("2 KB", page.ViewModel.SelectedTotalLabel);
        Assert.True(page.ViewModel.CanClean);
    }

    /// <summary>§7 sorts by size, and each row is placed as it lands rather than the list being sorted at the end.</summary>
    [Fact]
    public void RowsArePlacedLargestFirstAsTheyArrive()
    {
        FakeCleanupProvider small = new("small"), large = new("large"), medium = new("medium");
        using var page = new StoragePage([small, large, medium]);
        small.Steps = [Rows.Folder("s", 10)];
        large.Steps = [Rows.Folder("l", 900)];
        medium.Steps = [Rows.Folder("m", 450)];
        var placed = new List<int>();
        page.ViewModel.Findings.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                placed.Add(e.NewStartingIndex);
            }
        };

        page.Scan();

        Assert.Equal(["large", "medium", "small"], page.ViewModel.Findings.Select(row => row.Finding.Provider.Id));
        Assert.Equal([0, 0, 1], placed);
        Assert.Equal([100.0, 50.0], page.ViewModel.Findings.Take(2).Select(row => row.SharePercent));
    }

    /// <summary>
    /// A scan that fails after some rows have landed leaves those rows on the page, and the Selected
    /// figure has to describe them. It used to be totalled only at the end of a scan that succeeded,
    /// so it went on stating the previous scan's figure beside rows that no longer held it.
    /// </summary>
    [Fact]
    public void AScanThatFailsPartWayTotalsTheRowsItBuilt()
    {
        var cache = new FakeCleanupProvider("cache");
        var broken = new FakeCleanupProvider("broken");
        using var page = new StoragePage([cache, broken]);
        cache.Steps = [page.Cache("a", 1024)];
        page.Scan();
        Assert.Equal("1 KB", page.ViewModel.SelectedTotalLabel);

        cache.Steps = [page.Cache("a", 3072)];
        broken.PlanFailure = new IOException("The device is not ready.");
        page.Scan();

        Assert.StartsWith("Scan failed", page.ViewModel.Status);
        Assert.Equal(InfoBarSeverity.Error, page.ViewModel.StatusSeverity);
        Assert.Single(page.ViewModel.Findings);
        Assert.Equal("3 KB", page.ViewModel.SelectedTotalLabel);
    }

    /// <summary>
    /// A scan cancelled before any row lands leaves no rows, so nothing is selected. The previous
    /// scan's figure stood beside an empty list.
    /// </summary>
    [Fact]
    public void AScanCancelledBeforeAnyRowLandsSelectsNothing()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("a", 1024)];
        page.Scan();

        cache.PlanFailure = new OperationCanceledException();
        page.Scan();

        Assert.Equal("Scan cancelled. Nothing was changed.", page.ViewModel.Status);
        Assert.Empty(page.ViewModel.Findings);
        Assert.Equal("0 B", page.ViewModel.SelectedTotalLabel);
        Assert.False(page.ViewModel.CanClean);
    }

    /// <summary>
    /// Both filters are on by default and each hides its own rows as they land. A row waiting for a
    /// folder is absent too, and is never hidden: it is the one row that says what the user can do.
    /// </summary>
    [Fact]
    public void EachRowIsFilteredAsItLandsAndAgainWhenAFilterChanges()
    {
        var missing = new FakeCleanupProvider("missing") { IsPresent = false };
        var waiting = new FakeCleanupProvider("waiting") { IsPresent = false, IsAwaitingSourceFolders = true };
        var clear = new FakeCleanupProvider("clear");
        var ready = new FakeCleanupProvider("ready") { Steps = [Rows.Folder("r", 10)] };
        using var page = new StoragePage([missing, waiting, clear, ready]);

        page.Scan();

        Assert.False(page.Row("missing").IsListed);
        Assert.False(page.Row("clear").IsListed);
        Assert.True(page.Row("waiting").IsListed);
        Assert.True(page.Row("ready").IsListed);

        page.ViewModel.ShowNotInstalled = true;
        Assert.True(page.Row("missing").IsListed);
        Assert.False(page.Row("clear").IsListed);

        page.ViewModel.ShowAlreadyClear = true;
        Assert.True(page.Row("clear").IsListed);
    }

    /// <summary>
    /// An empty list says which kind of empty it is. Before a scan it says what to press; after one
    /// whose every row the filters hide, it says the filters did it. A filter change raises no change
    /// to the list itself, so the page has to announce the empty state on its own.
    /// </summary>
    [Fact]
    public void AnEmptyListSaysWhetherTheFiltersEmptiedIt()
    {
        var clear = new FakeCleanupProvider("clear");
        using var page = new StoragePage([clear]);

        Assert.True(page.ViewModel.HasNothingListed);
        Assert.Equal("Nothing scanned yet", page.ViewModel.EmptyStateTitle);

        page.Scan();

        Assert.True(page.ViewModel.HasNothingListed);
        Assert.Equal("Every row is hidden", page.ViewModel.EmptyStateTitle);

        var raised = page.ViewModel.Notifications();
        page.ViewModel.ShowAlreadyClear = true;

        Assert.False(page.ViewModel.HasNothingListed);
        Assert.Contains(nameof(page.ViewModel.HasNothingListed), raised);
        Assert.Contains(nameof(page.ViewModel.EmptyStateTitle), raised);
    }

    /// <summary>
    /// The Elevate button is offered before any scan to a process without the rights, never to one
    /// holding them, and after a scan only where elevating would change something on it.
    /// </summary>
    [Fact]
    public void ElevatingIsOfferedFromTheRightsThePageWasGiven()
    {
        var cache = new FakeCleanupProvider("cache") { Steps = [Rows.Folder("a", 10)] };
        using var unelevated = new StoragePage([cache]);
        using var elevated = new StoragePage([cache], isElevated: true);

        Assert.True(unelevated.ViewModel.CanElevate);
        Assert.False(elevated.ViewModel.CanElevate);

        unelevated.Scan();
        Assert.False(unelevated.ViewModel.CanElevate);

        cache.Steps = [Rows.Folder("a", 10, requiresElevation: true)];
        unelevated.Scan();
        Assert.True(unelevated.ViewModel.CanElevate);
    }

    /// <summary>
    /// A row's dialog can outlive a rescan and still hold the row it was opened for. A list for that
    /// row would take ticks nothing reads, and an item unticked there would still be deleted by the
    /// row that replaced it.
    /// </summary>
    [Fact]
    public void TheItemsOfARowNoLongerOnThePageAreNotListed()
    {
        var cache = new FakeCleanupProvider("cache") { Steps = [Rows.Folder("a", 10), Rows.Folder("b", 10)] };
        using var page = new StoragePage([cache]);
        page.Scan();
        var before = page.Row("cache");
        page.ViewModel.ShowItems(before);
        Assert.NotNull(page.ViewModel.ShownItems);

        page.Scan();

        Assert.Null(page.ViewModel.ShownItems);
        page.ViewModel.ShowItems(before);
        Assert.Null(page.ViewModel.ShownItems);

        page.ViewModel.ShowItems(page.Row("cache"));
        Assert.Same(page.Row("cache"), page.ViewModel.ShownItems!.Row);
    }

    /// <summary>
    /// The bar states what is ticked, so once a scan has put its sentence there, ticking a row moves
    /// the figure in it.
    /// </summary>
    [Fact]
    public void TheScansSentenceFollowsTheTicking()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("a", 1024), page.Cache("b", 2048)];
        page.Scan();
        Assert.Equal("3 KB can be reclaimed, 3 KB selected. Review the rows, then Clean.", page.ViewModel.Status);

        page.Row("cache").Steps[1].IsSelected = false;

        Assert.Equal("3 KB can be reclaimed, 1 KB selected. Review the rows, then Clean.", page.ViewModel.Status);
        Assert.Equal("1 KB", page.ViewModel.SelectedTotalLabel);
    }

    /// <summary>A tick is remembered as it is made, so a later scan starts where the user left it.</summary>
    [Fact]
    public void ATickIsRememberedForTheNextScan()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("a", 1024), page.Cache("b", 2048)];
        page.Scan();

        page.Row("cache").Steps[1].IsSelected = false;
        page.Scan();

        Assert.Equal([true, false], page.Row("cache").Steps.Select(step => step.IsSelected));
        Assert.Equal("1 KB", page.ViewModel.SelectedTotalLabel);
    }

    /// <summary>The capacity and the free space are the volume's own, read through the seam rather than off this machine.</summary>
    [Fact]
    public void TheCapacityBarIsReadFromTheProfilesVolume()
    {
        using var page = new StoragePage([], freeBytes: 250_000);

        Assert.Equal(StoragePage.Capacity, page.ViewModel.TotalSpace);
        Assert.Equal(250_000, page.ViewModel.FreeSpaceNow);
        Assert.Equal(75.0, page.ViewModel.UsedPercent);
        Assert.Contains(page.Environment.UserProfile, page.Volumes.MountPointQueries);
    }
}
