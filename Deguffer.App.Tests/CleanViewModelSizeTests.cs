using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Sizes;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Storage page wires each provider's size history (#358): which scans add to it, what a row
/// says about its change, that a change ticks nothing, and what a removal in Settings does to the
/// rows on screen. What counts as a figure and how a change is worded is Core's, and tested there.
/// </summary>
public sealed class CleanViewModelSizeTests
{
    /// <summary>
    /// The first scan has nothing to compare with and states no change. The second states how far
    /// the cause grew since the first, and is the newest figure kept.
    /// </summary>
    [Fact]
    public void ASecondScanStatesHowFarEachRowGrew()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [Rows.Folder("a", 1024)];

        page.Scan();

        Assert.False(page.Row("cache").HasChange);
        Assert.Null(page.Row("cache").ChangeTip);
        Assert.Equal(1024, Assert.Single(page.Sizes.Of("cache")).Bytes);

        cache.Steps = [Rows.Folder("a", 4096)];
        page.Scan();

        var row = page.Row("cache");
        Assert.True(row.HasChange);
        Assert.StartsWith("+3 KB since ", row.ChangeLabel, StringComparison.Ordinal);
        Assert.Equal(row.ChangeLabel, row.ChangeTip);
        Assert.Equal(4096, page.Sizes.Of("cache")[^1].Bytes);
    }

    /// <summary>
    /// §7.1: growth is a number. A Tier 2 cause that grew arrives unticked, as it would without the
    /// history, and a Tier 1 cause that shrank arrives ticked.
    /// </summary>
    [Fact]
    public void AChangeTicksNothing()
    {
        var grew = new FakeCleanupProvider("grew", SafetyTier.RegenerableWithCost);
        var shrank = new FakeCleanupProvider("shrank");
        using var page = new StoragePage([grew, shrank]);
        grew.Steps = [Rows.Folder("g", 1024)];
        shrank.Steps = [Rows.Folder("s", 8192)];

        page.Scan();
        bool grewBefore = page.Row("grew").IsSelected, shrankBefore = page.Row("shrank").IsSelected;

        grew.Steps = [Rows.Folder("g", 1024 * 1024)];
        shrank.Steps = [Rows.Folder("s", 1024)];
        page.Scan();

        Assert.True(page.Row("grew").HasChange);
        Assert.True(page.Row("shrank").HasChange);
        Assert.False(page.Row("grew").IsSelected);
        Assert.Equal(grewBefore, page.Row("grew").IsSelected);
        Assert.True(page.Row("shrank").IsSelected);
        Assert.Equal(shrankBefore, page.Row("shrank").IsSelected);
    }

    /// <summary>A scan of chosen locations adds only theirs, and every other provider keeps the figure it had.</summary>
    [Fact]
    public async Task AChosenScanKeepsOnlyWhatItMeasured()
    {
        FakeCleanupProvider chosen = new("chosen"), other = new("other");
        using var page = new StoragePage([chosen, other]);
        chosen.Steps = [Rows.Folder("c", 1024)];
        other.Steps = [Rows.Folder("o", 2048)];
        page.Sizes.Record(
            [new Finding(other, IsPresent: true, await other.PlanAsync())],
            DateTime.UtcNow.AddDays(-3),
            elevated: false);
        var before = Assert.Single(page.Sizes.Of("other"));

        page.ViewModel.ScanChoice.Choose([chosen], chosen: true);
        page.ScanChosen();

        Assert.Single(page.ViewModel.Findings);
        Assert.Single(page.Sizes.Of("chosen"));
        Assert.Equal(before, Assert.Single(page.Sizes.Of("other")));
    }

    /// <summary>
    /// The provider's own history, as its dialog lists it: every kept figure oldest first, each with
    /// its change from the one before, and bars against the largest.
    /// </summary>
    [Fact]
    public async Task AProvidersHistoryListsEveryKeptFigure()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [Rows.Folder("a", 1024)];
        page.Sizes.Record([new Finding(cache, IsPresent: true, await cache.PlanAsync())], DateTime.UtcNow.AddDays(-2), elevated: false);

        cache.Steps = [Rows.Folder("a", 4096)];
        page.Scan();

        var history = page.ViewModel.Sizes.HistoryOf(page.Row("cache"));

        Assert.Equal(2, history.Count);
        Assert.Equal(("1 KB", string.Empty, 25.0), (history[0].Size, history[0].Change, history[0].Share));
        Assert.Equal(("4 KB", "+3 KB", 100.0), (history[1].Size, history[1].Change, history[1].Share));
        Assert.EndsWith(": 4 KB, +3 KB", history[1].Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removing the history in Settings takes every change off the rows on screen, because the user
    /// asked for it to be gone, and the next scan has nothing to compare with.
    /// </summary>
    [Fact]
    public void RemovingTheHistoryInSettingsTakesTheChangesOffTheRows() => UiThread.Run(async () =>
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [Rows.Folder("a", 1024)];
        await page.ViewModel.PreviewCommand.ExecuteAsync(null);
        cache.Steps = [Rows.Folder("a", 2048)];
        await page.ViewModel.PreviewCommand.ExecuteAsync(null);
        Assert.True(page.Row("cache").HasChange);

        var settings = new SizeHistorySettingsViewModel(page.Sizes);
        await settings.RefreshAsync(CancellationToken.None);
        Assert.True(settings.HasAny);

        settings.RemoveAll();

        Assert.False(settings.RemoveFailed);
        Assert.False(settings.HasAny);
        Assert.False(page.Row("cache").HasChange);

        cache.Steps = [Rows.Folder("a", 4096)];
        await page.ViewModel.PreviewCommand.ExecuteAsync(null);

        Assert.False(page.Row("cache").HasChange);
    });

    /// <summary>
    /// A file the store cannot read gives no row a change, rather than a change against part of it:
    /// the cache's own figure is whole, and the entry after it is not one the store would write.
    /// </summary>
    [Fact]
    public void AnUnreadableHistoryGivesNoChange()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [Rows.Folder("a", 1024)];
        var folder = Directory.CreateDirectory(Path.Combine(page.Environment.LocalAppData, "Deguffer"));
        const string Point = """{"takenUtc":"2026-09-01T09:00:00Z","bytes":512,"entries":1,"approximate":false,"ceiling":false,"elevated":false}""";
        File.WriteAllText(
            Path.Combine(folder.FullName, "provider-sizes.json"),
            "{\"format\":1,\"providers\":{\"cache\":[" + Point + "],\"other\":[" + Point.Replace("512", "-1", StringComparison.Ordinal) + "]}}");

        page.Scan();

        Assert.False(page.Row("cache").HasChange);
        Assert.Equal(1024, Assert.Single(page.Sizes.Of("cache")).Bytes);
    }
}
