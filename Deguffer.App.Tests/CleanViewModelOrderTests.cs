using System.Collections.Specialized;
using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The Storage page's order: rows placed by the order the user chose as they land, and a finished
/// list moved to a new order in place, so no row is re-created and every row keeps its ticks.
/// </summary>
public class CleanViewModelOrderTests
{
    /// <summary>
    /// The order chosen before a scan places each row as it lands. The providers finish in the reverse
    /// of their names' order, and the sizes run against the names, so neither arrival nor size could
    /// produce this list.
    /// </summary>
    [Fact]
    public void RowsArePlacedInTheChosenOrderAsTheyArrive()
    {
        FakeCleanupProvider gamma = new("g") { Name = "Gamma" }, alpha = new("a") { Name = "alpha" }, beta = new("b") { Name = "Beta" };
        using var page = new StoragePage([gamma, alpha, beta]);
        gamma.Steps = [Rows.Folder("g", 900)];
        alpha.Steps = [Rows.Folder("a", 10)];
        beta.Steps = [Rows.Folder("b", 450)];
        alpha.WhilePlanning = () => Thread.Sleep(120);
        beta.WhilePlanning = () => Thread.Sleep(60);
        var placed = new List<int>();
        page.ViewModel.Findings.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                placed.Add(e.NewStartingIndex);
            }
        };

        page.ViewModel.Order = StorageOrder.Name;
        page.Scan();

        Assert.Equal(["a", "b", "g"], Ids(page.ViewModel.Findings));
        Assert.Equal([0, 0, 0], placed);
    }

    /// <summary>
    /// Changing the order under a finished list moves the rows that have to move, and only that: no
    /// row is removed and built again, the list is never reset, and the ticks stay as they were. The drawn list
    /// follows, a row the filters hide included, so it is reordered too and not left behind.
    /// </summary>
    [Fact]
    public void ChangingTheOrderMovesEachRowInPlace()
    {
        FakeCleanupProvider zeta = new("z") { Name = "Zeta" }, eta = new("e") { Name = "Eta" }, missing = new("m") { Name = "Mu", IsPresent = false };
        using var page = new StoragePage([zeta, eta, missing]);
        zeta.Steps = [Rows.Folder("z", 900)];
        eta.Steps = [Rows.Folder("e", 100)];
        page.Scan();
        var rows = page.ViewModel.Findings.ToList();
        page.Row("e").IsSelected = false;
        Assert.True(page.Row("z").IsSelected);
        Assert.Equal(["z", "e", "m"], Ids(page.ViewModel.Findings));
        Assert.Equal(["z", "e"], Ids(page.ViewModel.Listed));

        var findingChanges = new List<NotifyCollectionChangedAction>();
        var listedChanges = new List<NotifyCollectionChangedAction>();
        page.ViewModel.Findings.CollectionChanged += (_, e) => findingChanges.Add(e.Action);
        page.ViewModel.Listed.CollectionChanged += (_, e) => listedChanges.Add(e.Action);

        page.ViewModel.Order = StorageOrder.Name;

        Assert.Equal(["e", "m", "z"], Ids(page.ViewModel.Findings));
        Assert.Equal(["e", "z"], Ids(page.ViewModel.Listed));
        Assert.All(findingChanges, action => Assert.Equal(NotifyCollectionChangedAction.Move, action));
        Assert.All(listedChanges, action => Assert.Equal(NotifyCollectionChangedAction.Move, action));
        Assert.NotEmpty(listedChanges);
        Assert.Equal(rows.OrderBy(row => row.Name, StringComparer.InvariantCultureIgnoreCase), page.ViewModel.Findings);
        Assert.False(page.Row("e").IsSelected);
        Assert.True(page.Row("z").IsSelected);

        page.ViewModel.Order = StorageOrder.Size;

        Assert.Equal(["z", "e", "m"], Ids(page.ViewModel.Findings));
    }

    /// <summary>
    /// By tier, Tier 1 leads and the largest leads within each tier. The largest row overall is the
    /// Tier 3 one, so a size order would put it first.
    /// </summary>
    [Fact]
    public void ByTierTheSaferTiersLeadAndTheLargestLeadsWithinEach()
    {
        FakeCleanupProvider logs = new("logs", SafetyTier.UserData), small = new("small"), large = new("large"), costly = new("costly", SafetyTier.RegenerableWithCost);
        using var page = new StoragePage([logs, small, large, costly]);
        logs.Steps = [Rows.Folder("l", 9_000)];
        small.Steps = [Rows.Folder("s", 10)];
        large.Steps = [Rows.Folder("g", 500)];
        costly.Steps = [Rows.Folder("c", 800)];
        page.Scan();

        page.ViewModel.Order = StorageOrder.Tier;

        Assert.Equal(["large", "small", "costly", "logs"], Ids(page.ViewModel.Findings));
    }

    /// <summary>
    /// A stored order no member names is taken as the shipped one, and the page reads it back so its
    /// picker shows the order the list is really in.
    /// </summary>
    [Fact]
    public void AnOrderNoMemberNamesIsTakenAsTheSizeOrder()
    {
        FakeCleanupProvider b = new("b") { Name = "B" }, a = new("a") { Name = "A" };
        using var page = new StoragePage([b, a]);
        b.Steps = [Rows.Folder("b", 900)];
        a.Steps = [Rows.Folder("a", 100)];
        page.ViewModel.Order = StorageOrder.Name;
        page.Scan();

        page.ViewModel.Order = (StorageOrder)9;

        Assert.Equal(StorageOrder.Size, page.ViewModel.Order);
        Assert.Equal(["b", "a"], Ids(page.ViewModel.Findings));
    }

    private static IEnumerable<string> Ids(IEnumerable<ViewModels.FindingViewModel> rows) =>
        rows.Select(row => row.Finding.Provider.Id);
}
