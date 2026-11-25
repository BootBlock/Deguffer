using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// What a screen reader calls a Storage row's checkbox. A reader tabbing down the list stops on the
/// checkbox and nowhere else on the row, so its name carries the two facts the decision rests on.
/// </summary>
public class FindingSelectionNameTests
{
    private static readonly FakeCleanupProvider Cache = new("cache") { Name = "npm cache" };

    private static readonly FakeCleanupProvider Downloads = new("downloads", SafetyTier.RegenerableWithCost) { Name = "Downloads" };

    [Fact]
    public void TheCheckBoxIsNamedForTheRowItsFigureAndItsTier()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10), Rows.Folder("b", 20)));

        Assert.Equal($"npm cache, {row.SizeLabel}, Rebuilds itself", row.SelectionName);
        Assert.StartsWith("Downloads, ", Rows.Row(Rows.Found(Downloads, Rows.Folder("a", 10))).SelectionName);
        Assert.EndsWith(", Costly to rebuild", Rows.Row(Rows.Found(Downloads, Rows.Folder("a", 10))).SelectionName);
    }

    /// <summary>
    /// The figure moves with every tick, from the whole to the part a clean takes, and the name moves
    /// with it: a reader who unticks one item hears the new figure, not the one the row opened with.
    /// </summary>
    [Fact]
    public void TheNameFollowsTheFigureAsItemsAreTicked()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10), Rows.Folder("b", 20)));
        var before = row.SelectionName;
        var raised = row.Notifications();

        row.Steps[0].IsSelected = false;

        Assert.NotEqual(before, row.SelectionName);
        Assert.Contains(row.SizeLabel, row.SelectionName, StringComparison.Ordinal);
        Assert.Contains(nameof(FindingViewModel.SelectionName), raised);
    }

    /// <summary>A row with no installation has only a dash for a figure, which says nothing aloud.</summary>
    [Fact]
    public void ARowWithNoInstallationIsNamedWithoutAFigure()
    {
        var row = Rows.Row(new Finding(Cache, IsPresent: false, Plan: null));

        Assert.Equal("npm cache, Rebuilds itself", row.SelectionName);
    }
}
