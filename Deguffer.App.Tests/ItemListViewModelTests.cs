using Deguffer.App.ViewModels;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How a row's item list stays in step with the row: its figures, its headings, the order it
/// announces a new search in, and letting go of the row when it closes. The ordering, the search and
/// the tri-state checkboxes are Core's; see <see cref="Core.Choosing.ItemSelection"/>.
/// </summary>
public class ItemListViewModelTests
{
    private static readonly FakeCleanupProvider Workspaces = new("workspaces");

    [Fact]
    public void TheCountSaysHowManyAreTickedOfThoseThatCanBe()
    {
        var row = Rows.Row(Rows.Found(
            Workspaces, Rows.Folder("a", 1024), Rows.Folder("b", 1024), Rows.Folder("c", 1024, requiresElevation: true)));
        using var list = new ItemListViewModel(row);

        Assert.Equal("2 of 2 selected, 2 KB", list.SelectionLabel);
    }

    /// <summary>A tick made anywhere reaches the list through the row, and the figures follow it.</summary>
    [Fact]
    public void TheFiguresFollowATickMadeOnTheRow()
    {
        var row = Rows.Row(Rows.Found(Workspaces, Rows.Folder("a", 1024), Rows.Folder("b", 1024)));
        using var list = new ItemListViewModel(row);
        var raised = list.Notifications();

        row.Steps[0].IsSelected = false;

        Assert.Equal("1 of 2 selected, 1 KB", list.SelectionLabel);
        Assert.Null(list.AllShownState);
        Assert.Contains(nameof(ItemListViewModel.SelectionLabel), raised);
    }

    /// <summary>A checkbox standing for items none of which can be ticked would be one no click can change.</summary>
    [Fact]
    public void TheCheckboxOverEveryItemIsOffWhereNoShownItemCanBeTicked()
    {
        var row = Rows.Row(Rows.Found(
            Workspaces, Rows.Folder("alpha", 10), Rows.Folder("beta", 10, requiresElevation: true)));
        using var list = new ItemListViewModel(row);

        Assert.True(list.CanToggleAllShown);

        list.Query = "beta";

        Assert.False(list.CanToggleAllShown);
    }

    /// <summary>The checkbox over the list covers what the search shows, so a click never reaches a hidden item.</summary>
    [Fact]
    public void TheCheckboxOverEveryItemReachesOnlyWhatTheSearchShows()
    {
        var row = Rows.Row(Rows.Found(Workspaces, Rows.Folder("alpha", 10), Rows.Folder("beta", 10)));
        using var list = new ItemListViewModel(row);
        list.Query = "alpha";

        list.ToggleAllShownCommand.Execute(null);

        Assert.Equal([false, true], row.Steps.Select(step => step.IsSelected));
        Assert.Equal(1, list.HiddenSelected);
    }

    /// <summary>
    /// A listener to the groups reads the flat list beside them, so the flat list is in place first. The
    /// other order hands that listener the previous search's items.
    /// </summary>
    [Fact]
    public void ANewSearchIsInPlaceBeforeItsGroupsAreAnnounced()
    {
        var row = Rows.Row(Rows.Found(Workspaces, Rows.Folder("alpha", 10), Rows.Folder("beta", 10)));
        using var list = new ItemListViewModel(row);
        var seen = new List<int>();
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ItemListViewModel.Shown))
            {
                seen.Add(list.ShownItems.Count);
            }
        };

        list.Query = "beta";

        Assert.Equal([1], seen);
    }

    /// <summary>
    /// A closed list stops following its row, which otherwise keeps it alive and pays for its refresh on
    /// every tick for as long as the row is on the page.
    /// </summary>
    [Fact]
    public void AClosedListNoLongerFollowsItsRow()
    {
        var row = Rows.Row(Rows.Found(Workspaces, Rows.Folder("a", 1024), Rows.Folder("b", 1024)));
        var list = new ItemListViewModel(row);
        list.Dispose();
        var raised = list.Notifications();

        row.Steps[0].IsSelected = false;

        Assert.Empty(raised);
    }

    /// <summary>
    /// A provider that puts some items under headings and some under none still names the second group,
    /// or its items would read as part of the heading above them.
    /// </summary>
    [Fact]
    public void ItemsUnderNoHeadingAreGroupedAsOtherItems()
    {
        var row = Rows.Row(Rows.Found(
            Workspaces, Rows.Folder("a", 4096, group: "Projects"), Rows.Folder("b", 1024)));
        using var list = new ItemListViewModel(row);

        Assert.True(list.IsGrouped);
        Assert.Equal(["Projects", "Other items"], list.Shown.Select(group => group.Title));
    }

    /// <summary>A heading states what its items come to, counting only those the search shows.</summary>
    [Fact]
    public void AHeadingSumsOnlyTheItemsTheSearchShows()
    {
        var row = Rows.Row(Rows.Found(
            Workspaces,
            Rows.Folder("alpha", 1024, group: "Projects"),
            Rows.Folder("beta", 2048, group: "Projects"),
            Rows.Folder("alpine", 4096, group: "Projects")));
        using var list = new ItemListViewModel(row);

        Assert.Equal("3 items, 7 KB", Assert.Single(list.Shown).Summary);

        list.Query = "alp";

        Assert.Equal("2 items, 5 KB", Assert.Single(list.Shown).Summary);
    }

    /// <summary>A click on a heading is one change to the row, whatever the number of items under it.</summary>
    [Fact]
    public void AClickOnAHeadingIsOneChange()
    {
        var row = Rows.Row(Rows.Found(
            Workspaces, [.. Enumerable.Range(0, 50).Select(i => Rows.Folder($"w{i}", 10, group: "Projects"))]));
        using var list = new ItemListViewModel(row);
        var events = row.SelectionEvents();

        Assert.Single(list.Shown).ToggleCommand.Execute(null);

        Assert.All(row.Steps, step => Assert.False(step.IsSelected));
        Assert.Single(events);
        Assert.False(list.Shown[0].State);
    }
}
