using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Testing;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// Keeping and releasing items from the Storage page: the rows and totals that follow, a change the
/// page refuses while a run is using the list, and a change that could not be saved said out loud.
/// </summary>
public class CleanViewModelKeepTests
{
    /// <summary>A page scanned once, showing one row of two items that can be kept: "a" of 1 KB and "b" of 2 KB.</summary>
    private static StoragePage Scanned(out FakeCleanupProvider cache, string? storedKeepList = null)
    {
        cache = new FakeCleanupProvider("cache");
        var page = new StoragePage([cache], storedKeepList: storedKeepList);
        cache.Steps = [page.Cache("a", 1024, key: "a"), page.Cache("b", 2048, key: "b")];
        page.Scan();

        return page;
    }

    /// <summary>Makes the next write of the keep list fail, as a full or read-only folder would.</summary>
    private static void RefuseSaving(StoragePage page) =>
        Directory.CreateDirectory(Path.Combine(page.Environment.LocalAppData, "Deguffer", "keep.json.tmp"));

    /// <summary>
    /// A kept item leaves the row's plan, the page's totals and the scan's sentence at once, and the
    /// keep reaches disk so a restart still honours it.
    /// </summary>
    [Fact]
    public void KeepingAnItemTakesItOutOfTheRunAndTheFigures()
    {
        using var page = Scanned(out _);
        var row = page.Row("cache");

        page.ViewModel.ToggleKeep(row, row.Steps[0]);

        Assert.True(row.Steps[0].IsKept);
        Assert.Equal("2 KB", page.ViewModel.SelectedTotalLabel);
        Assert.Equal("2 KB can be reclaimed, 2 KB selected. Review the rows, then Clean.", page.ViewModel.Status);
        Assert.Equal(["a"], new KeepStore(page.Environment).Load().KeysFor("cache"));
    }

    /// <summary>A kept item is not deleted by the next clean, and the clean proves it is still standing (§5.6).</summary>
    [Fact]
    public void AKeptItemSurvivesTheNextClean()
    {
        using var page = Scanned(out var cache);
        var row = page.Row("cache");
        var kept = row.Steps[0].Step;

        page.ViewModel.ToggleKeep(row, row.Steps[0]);
        page.Clean();

        Assert.True(Directory.Exists(kept.Subjects[0]));
        Assert.DoesNotContain(Assert.Single(cache.Executed).Steps, step => step.SelectionKey == kept.SelectionKey);
        Assert.Equal("All protected paths survived.", page.ViewModel.RunStatement);
    }

    /// <summary>
    /// The run in progress was built from the list as it stood, so a keep clicked mid-run would say an
    /// item is protected while the plan executing deletes it. The buttons go off, and a click that
    /// lands anyway changes nothing.
    /// </summary>
    [Fact]
    public void TheKeepListCannotChangeWhileAScanOrACleanIsRunning()
    {
        using var page = Scanned(out _);
        var row = page.Row("cache");
        Assert.All(row.Steps, step => Assert.True(step.CanChangeKeepList));

        page.ViewModel.IsBusy = true;
        page.ViewModel.ToggleKeep(row, row.Steps[0]);

        Assert.All(row.Steps, step => Assert.False(step.CanChangeKeepList));
        Assert.False(row.Steps[0].IsKept);
        Assert.Empty(page.Keeps.Current.Items);

        page.ViewModel.IsBusy = false;

        Assert.All(row.Steps, step => Assert.True(step.CanChangeKeepList));
    }

    /// <summary>
    /// Each row's bar is drawn against the largest row. A row shrunk by keeping an item stays where it
    /// stands rather than the list being reshuffled, so the largest row is not always the first one.
    /// </summary>
    [Fact]
    public void ARowShrunkByAKeepNoLongerSetsTheScaleOfTheBars()
    {
        var big = new FakeCleanupProvider("big");
        var small = new FakeCleanupProvider("small");
        using var page = new StoragePage([big, small]);
        big.Steps = [page.Cache("a", 4096, key: "a")];
        small.Steps = [page.Cache("b", 1024)];
        page.Scan();
        Assert.Equal(25.0, page.Row("small").SharePercent);

        var row = page.Row("big");
        page.ViewModel.ToggleKeep(row, row.Steps[0]);

        Assert.Same(row, page.ViewModel.Findings[0]);
        Assert.Equal(100.0, page.Row("small").SharePercent);
        Assert.Equal(0.0, row.SharePercent);
    }

    /// <summary>
    /// A keep that could not be saved is said, as a warning, because an item offered again after a
    /// restart is the failure the keep list exists to prevent. It stays said through later ticking.
    /// </summary>
    [Fact]
    public void AKeepThatCouldNotBeSavedIsSaidAndStaysSaid()
    {
        using var page = Scanned(out _);
        var row = page.Row("cache");
        RefuseSaving(page);

        page.ViewModel.ToggleKeep(row, row.Steps[0]);

        Assert.True(row.Steps[0].IsKept);
        Assert.StartsWith("Kept a for now, but Deguffer could not save the keep list", page.ViewModel.Status);
        Assert.Equal(InfoBarSeverity.Warning, page.ViewModel.StatusSeverity);

        row.Steps[1].IsSelected = false;

        Assert.StartsWith("Kept a for now", page.ViewModel.Status);
    }

    /// <summary>A release that could not be saved leaves the item kept, and says so.</summary>
    [Fact]
    public void AReleaseThatCouldNotBeSavedLeavesTheItemKept()
    {
        using var page = Scanned(out _);
        var row = page.Row("cache");
        page.ViewModel.ToggleKeep(row, row.Steps[0]);
        RefuseSaving(page);

        page.ViewModel.ToggleKeep(row, row.Steps[0]);

        Assert.True(row.Steps[0].IsKept);
        Assert.Equal("a is still on your keep list: Deguffer could not save the change.", page.ViewModel.Status);
    }

    /// <summary>
    /// A saved list that could not be read is not written over, so every change is for this session
    /// only, and the sentence names the file that needs attention rather than the folder.
    /// </summary>
    [Fact]
    public void AnUnreadableSavedListMakesEveryChangeForThisSessionOnly()
    {
        using var page = Scanned(out _, storedKeepList: "{}");
        var row = page.Row("cache");

        page.ViewModel.ToggleKeep(row, row.Steps[0]);

        Assert.True(row.Steps[0].IsKept);
        Assert.StartsWith("Kept a for this session only.", page.ViewModel.Status);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(page.Environment.LocalAppData, "Deguffer", "keep.json")));
    }

    /// <summary>
    /// Settings can release an item while this page is away, and the page applies that on its way back.
    /// The same list applied again is nothing to do, and does nothing.
    /// </summary>
    [Fact]
    public void AKeepListChangedElsewhereIsAppliedOnceOnTheWayBack()
    {
        using var page = Scanned(out _);
        var row = page.Row("cache");

        page.Keeps.Keep(new KeptItem("cache", "cache", new ItemIdentity("b", "b")));
        page.ViewModel.ApplyKeepList();

        Assert.True(row.Steps[1].IsKept);
        Assert.Equal("1 KB", page.ViewModel.SelectedTotalLabel);

        var raised = page.ViewModel.Notifications();
        page.ViewModel.ApplyKeepList();

        Assert.Empty(raised);
    }
}
