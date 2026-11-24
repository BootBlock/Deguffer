using System.Collections.Specialized;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// The Storage page's scan: rows built as their providers finish, inside progress callbacks, placed and
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
        Assert.Equal("Clean 2 KB", page.ViewModel.Selection.CleanLabel);
        Assert.True(page.ViewModel.CanClean);
    }

    /// <summary>
    /// §5.5: while a provider measures, the bar names it with the bytes it has counted so far, as the
    /// planner reports them, so a long walk shows something moving. The planner posts the line by time,
    /// so the clock moves an interval while the provider is still being checked, and the bar shows the
    /// latest figure.
    /// </summary>
    [Fact]
    public void TheBarShowsWhatAProviderHasCountedWhileItIsChecked()
    {
        var clock = new ManualTimeProvider();
        var cache = new FakeCleanupProvider("cache")
        {
            Counted = [1024, 3 * 1024],
            // Longer than the interval the planner posts its line at, which is Core's own.
            WhilePlanning = () => clock.Advance(TimeSpan.FromSeconds(1)),
        };
        using var page = new StoragePage([cache], time: clock);
        var statuses = new List<string>();
        page.ViewModel.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(page.ViewModel.Status))
            {
                statuses.Add(page.ViewModel.Status);
            }
        };

        page.Scan();

        Assert.Contains($"Checking cache ({FreeSpace.Format(3 * 1024)} counted so far)…", statuses);
    }

    /// <summary>
    /// §7 sorts by size, and each row is placed as it lands rather than the list being sorted at the
    /// end. The providers are made to finish small, then large, then medium, so each row lands
    /// somewhere a plain append would not put it.
    /// </summary>
    [Fact]
    public void RowsArePlacedLargestFirstAsTheyArrive()
    {
        FakeCleanupProvider small = new("small"), large = new("large"), medium = new("medium");
        using var page = new StoragePage([small, large, medium]);
        small.Steps = [Rows.Folder("s", 10)];
        large.Steps = [Rows.Folder("l", 900)];
        medium.Steps = [Rows.Folder("m", 450)];
        page.FinishIn(small, large, medium);
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
    /// Rows of equal size stand in the providers' order, not in the order they finished, so the list is
    /// the same after every scan. Here the last provider finishes first and the first last.
    /// </summary>
    [Fact]
    public void RowsOfEqualSizeStandInTheProvidersOrderWhicheverFinishesFirst()
    {
        FakeCleanupProvider first = new("first"), second = new("second"), third = new("third");
        using var page = new StoragePage([first, second, third]);
        page.FinishIn(third, second, first);

        page.Scan();

        Assert.Equal(["first", "second", "third"], page.ViewModel.Findings.Select(row => row.Finding.Provider.Id));
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
        Assert.Equal("Clean 1 KB", page.ViewModel.Selection.CleanLabel);

        cache.Steps = [page.Cache("a", 3072)];
        broken.PlanFailure = new IOException("The device is not ready.");
        page.Scan();

        Assert.StartsWith("Scan failed", page.ViewModel.Status);
        Assert.Equal(InfoBarSeverity.Error, page.ViewModel.StatusSeverity);
        Assert.Single(page.ViewModel.Findings);
        Assert.Equal("Clean 3 KB", page.ViewModel.Selection.CleanLabel);
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
        Assert.Equal("Clean", page.ViewModel.Selection.CleanLabel);
        Assert.False(page.ViewModel.CanClean);
    }

    /// <summary>
    /// Both filters are on by default and each hides its own rows as they land. A row waiting for a
    /// folder is absent too, and is never hidden: it goes to the folded group below the list, under
    /// either filter. The list draws exactly the rows the filters let through, and a filter switched
    /// on adds its rows to it one by one rather than rebuilding it, so the rows already drawn stay
    /// where they are.
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

        Assert.Equal(PreviewPlace.Hidden, page.Row("missing").Place);
        Assert.Equal(PreviewPlace.Hidden, page.Row("clear").Place);
        Assert.Equal(PreviewPlace.AwaitingSourceFolders, page.Row("waiting").Place);
        Assert.Equal(PreviewPlace.Listed, page.Row("ready").Place);
        Assert.Equal(["ready"], Drawn(page));
        Assert.Equal(["waiting"], Grouped(page));

        var changes = new List<NotifyCollectionChangedAction>();
        page.ViewModel.Listed.CollectionChanged += (_, e) => changes.Add(e.Action);

        page.ViewModel.ShowNotInstalled = true;
        Assert.Equal(PreviewPlace.Listed, page.Row("missing").Place);
        Assert.Equal(PreviewPlace.Hidden, page.Row("clear").Place);
        Assert.Equal(["missing", "ready"], Drawn(page).Order());
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);

        page.ViewModel.ShowAlreadyClear = true;
        Assert.Equal(PreviewPlace.Listed, page.Row("clear").Place);
        Assert.Equal(["clear", "missing", "ready"], Drawn(page).Order());
        Assert.Equal(["waiting"], Grouped(page));

        static IEnumerable<string> Drawn(StoragePage page) =>
            page.ViewModel.Listed.Select(row => row.Finding.Provider.Id);

        static IEnumerable<string> Grouped(StoragePage page) =>
            page.ViewModel.AwaitingFolders.Rows.Select(row => row.Finding.Provider.Id);
    }

    /// <summary>
    /// The folded group says how many rows it holds, and a card whose only rows are in it is not an
    /// empty card: closed, the group still says what is folded away. A second scan starts the group
    /// again, once, as it does the list.
    /// </summary>
    [Fact]
    public void TheFoldedGroupCountsItsRowsAndKeepsTheCardFromReadingAsEmpty()
    {
        FakeCleanupProvider[] providers =
        [
            new("rust") { IsPresent = false, IsAwaitingSourceFolders = true },
            new("godot") { IsPresent = false, IsAwaitingSourceFolders = true },
        ];
        using var page = new StoragePage(providers);
        var group = page.ViewModel.AwaitingFolders;
        var raised = group.Notifications();

        Assert.False(group.HasRows);

        page.Scan();

        Assert.True(group.HasRows);
        Assert.Contains(nameof(group.HasRows), raised);
        Assert.Equal("2 locations with nothing to search until you add a source folder.", group.Summary);
        Assert.Empty(page.ViewModel.Listed);
        Assert.False(page.ViewModel.EmptyState.IsShown);

        var changes = new List<NotifyCollectionChangedAction>();
        group.Rows.CollectionChanged += (_, e) => changes.Add(e.Action);

        page.Scan();

        Assert.Equal(NotifyCollectionChangedAction.Reset, changes[0]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, changes);
        Assert.Equal(2, group.Rows.Count);
    }

    /// <summary>
    /// A second scan's rows are another scan's, so the drawn rows are reset once rather than removed
    /// one at a time. The reset is what tells the list that its scroll position points at rows that
    /// are gone, so a scan started partway down the list shows the new list from its top.
    /// </summary>
    [Fact]
    public void ASecondScanResetsTheDrawnRowsOnceRatherThanRemovingEach()
    {
        FakeCleanupProvider[] providers =
        [
            new("a") { Steps = [Rows.Folder("a", 30)] },
            new("b") { Steps = [Rows.Folder("b", 20)] },
            new("c") { Steps = [Rows.Folder("c", 10)] },
        ];
        using var page = new StoragePage(providers);

        page.Scan();

        var changes = new List<NotifyCollectionChangedAction>();
        page.ViewModel.Listed.CollectionChanged += (_, e) => changes.Add(e.Action);

        page.Scan();

        Assert.Equal(NotifyCollectionChangedAction.Reset, changes[0]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, changes);
        Assert.Equal(["a", "b", "c"], page.ViewModel.Listed.Select(row => row.Finding.Provider.Id));
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

        var empty = page.ViewModel.EmptyState;

        Assert.True(empty.IsShown);
        Assert.Equal("Nothing scanned yet", empty.Title);

        // The empty state says what to press, so the status bar does not say it again over it.
        Assert.Empty(page.ViewModel.Status);
        Assert.False(page.ViewModel.HasStatus);

        page.Scan();

        Assert.True(empty.IsShown);
        Assert.Equal("Every row is hidden", empty.Title);

        var raised = empty.Notifications();
        page.ViewModel.ShowAlreadyClear = true;

        Assert.False(empty.IsShown);
        Assert.Contains(nameof(empty.IsShown), raised);
        Assert.Contains(nameof(empty.Title), raised);
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
    /// Elevating ends this process, and a clean or a removal still running on another page would
    /// end with it, unverified and unreported. The button waits for it, and comes back when it ends.
    /// </summary>
    [Fact]
    public void ElevatingWaitsForAnActionRunningOnAnotherPage()
    {
        using var page = new StoragePage([new FakeCleanupProvider("cache")]);
        var elevate = page.ViewModel.ElevateAndRescanCommand;
        var raised = 0;
        elevate.CanExecuteChanged += (_, _) => raised++;

        var removal = page.Running.Begin(RunningAction.ExploreRemoval);

        Assert.False(elevate.CanExecute(null));

        removal.Dispose();

        Assert.True(elevate.CanExecute(null));
        Assert.Equal(2, raised);
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
        Assert.Equal("Clean 1 KB", page.ViewModel.Selection.CleanLabel);
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
        Assert.Equal("Clean 1 KB", page.ViewModel.Selection.CleanLabel);
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
