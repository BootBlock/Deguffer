using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The Storage page's selection menu as the page wires it: each entry ticks or clears the rows through
/// their own checkboxes, is remembered and totalled as one change, and is offered only where it would
/// change something and only while the rows are on screen and nothing is running. Which rows an entry
/// reaches is Core's, and <c>BulkSelectionRuleTests</c> proves it.
/// </summary>
public class StorageSelectionTests
{
    /// <summary>
    /// A page scanned once with three rows of 1 KB: a regenerable cache, which starts ticked, a cache
    /// that costs a download to rebuild, and user data, which both start clear.
    /// </summary>
    private static StoragePage Scanned()
    {
        var cache = new FakeCleanupProvider("cache");
        var sdk = new FakeCleanupProvider("sdk", SafetyTier.RegenerableWithCost);
        var logs = new FakeCleanupProvider("logs", SafetyTier.UserData);
        var page = new StoragePage([cache, sdk, logs]);
        cache.Steps = [page.Cache("cache", 1024)];
        sdk.Steps = [page.Cache("sdk", 1024)];
        logs.Steps = [page.Cache("logs", 1024)];
        page.Scan();

        return page;
    }

    private static StorageSelection Menu(StoragePage page) => page.ViewModel.Selection;

    private static void Run(Func<StorageSelection, CommunityToolkit.Mvvm.Input.IRelayCommand> entry, StoragePage page) =>
        UiThread.Run(() =>
        {
            entry(Menu(page)).Execute(null);
            return Task.CompletedTask;
        });

    /// <summary>What the stored selection says of each row, once every write has landed.</summary>
    private static Dictionary<string, bool> Stored(StoragePage page)
    {
        page.Selections.Flush();

        return new SelectionStore(page.Environment).Load().ToDictionary(pair => pair.Key, pair => pair.Value.IsSelected);
    }

    [Fact]
    public void SelectAllTicksEveryRowWithSomethingToGiveBack()
    {
        using var page = Scanned();

        Run(menu => menu.SelectAllCommand, page);

        Assert.All(page.ViewModel.Findings, row => Assert.True(row.IsSelected));
        Assert.Equal("Clean 3 KB", Menu(page).CleanLabel);
        Assert.True(page.ViewModel.CanClean);
    }

    /// <summary>A row of nothing but empty folders has no size, so "all" leaves it as the reader left it.</summary>
    [Fact]
    public void SelectAllLeavesARowWithNoSize()
    {
        var cache = new FakeCleanupProvider("cache", SafetyTier.RegenerableWithCost);
        var leftover = new FakeCleanupProvider("leftover", SafetyTier.RegenerableWithCost);
        using var page = new StoragePage([cache, leftover]);
        cache.Steps = [page.Cache("cache", 1024)];
        leftover.Steps = [page.Cache("leftover", 0) with { IsLeftover = true, Estimated = new ScanSize(0, 0, Entries: 3) }];
        page.Scan();

        Run(menu => menu.SelectAllCommand, page);

        Assert.True(page.Row("cache").IsSelected);
        Assert.False(page.Row("leftover").IsSelected);
    }

    /// <summary>
    /// An item the reader unticked inside a ticked row stays unticked through either entry that ticks,
    /// in the row and in what is remembered: the row shows no sign of it, so ticking it back would have
    /// the next clean delete what the reader chose to keep.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnItemUntickedInsideATickedRowStaysUnticked(bool all)
    {
        var cache = new FakeCleanupProvider("cache");
        var sdk = new FakeCleanupProvider("sdk", SafetyTier.RegenerableWithCost);
        using var page = new StoragePage([cache, sdk]);
        cache.Steps = [page.Cache("kept", 1024), page.Cache("taken", 1024)];
        sdk.Steps = [page.Cache("sdk", 1024)];
        page.Scan();
        var row = page.Row("cache");
        row.Steps[0].IsSelected = false;
        page.Row("sdk").SetTicked(!all);

        Run(menu => all ? menu.SelectAllCommand : menu.SelectRecommendedCommand, page);

        Assert.True(row.IsSelected);
        Assert.False(row.Steps[0].IsSelected);
        Assert.True(row.Steps[1].IsSelected);
        page.Selections.Flush();
        Assert.False(new SelectionStore(page.Environment).Load()["cache"].Steps[row.Steps[0].Step.SelectionKey]);
    }

    /// <summary>
    /// The Clean button reads its words from the selection, so they follow each tick and untick with
    /// a notification for the binding, rather than stating the total of the scan that built the rows.
    /// </summary>
    [Fact]
    public void TheCleanButtonStatesWhatIsTickedAsRowsAreTicked()
    {
        using var page = Scanned();
        var told = new List<string>();
        Menu(page).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StorageSelection.CleanLabel))
            {
                told.Add(Menu(page).CleanLabel);
            }
        };

        Assert.Equal("Clean 1 KB", Menu(page).CleanLabel);

        page.Row("sdk").SetTicked(true);
        page.Row("logs").SetTicked(true);
        page.Row("cache").SetTicked(false);
        page.Row("sdk").SetTicked(false);
        page.Row("logs").SetTicked(false);

        Assert.Equal(["Clean 2 KB", "Clean 3 KB", "Clean 2 KB", "Clean 1 KB", "Clean"], told);
    }

    [Fact]
    public void SelectNoneClearsEveryRow()
    {
        using var page = Scanned();
        Run(menu => menu.SelectAllCommand, page);

        Run(menu => menu.SelectNoneCommand, page);

        Assert.All(page.ViewModel.Findings, row => Assert.False(row.IsSelected));
        Assert.All(page.ViewModel.Findings, row => Assert.All(row.Steps, step => Assert.False(step.IsSelected)));
        Assert.Equal("Clean", Menu(page).CleanLabel);
        Assert.False(page.ViewModel.CanClean);
    }

    /// <summary>
    /// "Recommended" puts back the first scan's selection: the regenerable cache ticked, and the rows
    /// the reader ticked beyond it cleared.
    /// </summary>
    [Fact]
    public void SelectRecommendedPutsBackTheFirstScansSelection()
    {
        using var page = Scanned();
        page.Row("cache").SetTicked(false);
        page.Row("logs").SetTicked(true);

        Run(menu => menu.SelectRecommendedCommand, page);

        Assert.True(page.Row("cache").IsSelected);
        Assert.False(page.Row("sdk").IsSelected);
        Assert.False(page.Row("logs").IsSelected);
        Assert.Equal("Clean 1 KB", Menu(page).CleanLabel);
    }

    /// <summary>
    /// The rows a menu entry changed are remembered as any click is, so the next scan starts with them
    /// as the reader left them, and the scan's sentence follows the ticks.
    /// </summary>
    [Fact]
    public void AMenuEntryIsRememberedAndRestatedOnce()
    {
        using var page = Scanned();
        var changes = 0;
        var statuses = 0;
        Menu(page).Changed += (_, _) => changes++;
        page.ViewModel.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(CleanViewModel.Status))
            {
                statuses++;
            }
        };

        Run(menu => menu.SelectAllCommand, page);

        Assert.Equal(1, changes);
        Assert.Equal(1, statuses);
        Assert.Equal("3 KB can be cleaned, 3 KB selected. Review the rows, then Clean.", page.ViewModel.Status);
        // The cache started ticked and the entry did not touch it, so only the two it ticked are written.
        Assert.Equal(new Dictionary<string, bool> { ["sdk"] = true, ["logs"] = true }, Stored(page));
    }

    /// <summary>An entry that would change no tick is offered disabled, and becomes available again once one would.</summary>
    [Fact]
    public void AnEntryIsOfferedOnlyWhereItWouldChangeATick()
    {
        using var page = Scanned();
        Assert.True(Menu(page).SelectAllCommand.CanExecute(null));
        Assert.False(Menu(page).SelectRecommendedCommand.CanExecute(null));

        Run(menu => menu.SelectAllCommand, page);
        Assert.False(Menu(page).SelectAllCommand.CanExecute(null));
        Assert.True(Menu(page).SelectRecommendedCommand.CanExecute(null));

        Run(menu => menu.SelectNoneCommand, page);
        Assert.False(Menu(page).SelectNoneCommand.CanExecute(null));

        page.Row("cache").SetTicked(true);
        Assert.True(Menu(page).SelectNoneCommand.CanExecute(null));
    }

    /// <summary>Before any scan there is no row to change.</summary>
    [Fact]
    public void NothingIsOfferedBeforeAScan()
    {
        using var page = new StoragePage([new FakeCleanupProvider("cache")]);

        Assert.False(Menu(page).SelectAllCommand.CanExecute(null));
        Assert.False(Menu(page).SelectRecommendedCommand.CanExecute(null));
        Assert.False(Menu(page).SelectNoneCommand.CanExecute(null));
    }

    /// <summary>
    /// While a scan or a clean runs, the rows are being added or are about to be replaced, so the menu
    /// is refused, and an entry run anyway changes nothing.
    /// </summary>
    [Fact]
    public void TheMenuIsRefusedWhileThePageIsBusy()
    {
        using var page = Scanned();

        page.ViewModel.IsBusy = true;
        Assert.False(Menu(page).SelectNoneCommand.CanExecute(null));
        Run(menu => menu.SelectNoneCommand, page);
        Assert.True(page.Row("cache").IsSelected);

        page.ViewModel.IsBusy = false;
        Assert.True(Menu(page).SelectNoneCommand.CanExecute(null));
    }

    /// <summary>
    /// While one row's items are listed in place of the rows, the reader cannot see the rows the menu
    /// would change, so it is refused until they are back.
    /// </summary>
    [Fact]
    public void TheMenuIsRefusedWhileOneRowsItemsAreListed()
    {
        using var page = Scanned();
        var refreshed = 0;
        Menu(page).SelectAllCommand.CanExecuteChanged += (_, _) => refreshed++;

        page.ViewModel.ShowItems(page.Row("cache"));
        Assert.False(Menu(page).SelectAllCommand.CanExecute(null));

        page.ViewModel.CloseItems();
        Assert.True(Menu(page).SelectAllCommand.CanExecute(null));
        Assert.True(refreshed >= 2);
    }
}
