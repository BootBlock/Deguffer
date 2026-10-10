using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page wires what grew since the last scan (#260): which scans are recorded and
/// compared, what the panel says, where a row leads, and what a removal in Settings does to a
/// comparison on screen. What is compared and how it is worded is Core's, and tested there.
/// </summary>
public sealed class ExploreGrowthTests : IDisposable
{
    private const string Volume = @"\\?\Volume{0b6f5c1e-3a7d-4c2b-9e10-6d2f1a4b8c33}\";

    private const long Megabyte = 1024L * 1024;

    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// The first scan of a drive has nothing to compare with and says so. The second is compared with
    /// it, lists the folder that grew with its growth and the one removed as removed, and colours the
    /// map by it.
    /// </summary>
    [Fact]
    public void ASecondScanOfTheDriveIsComparedWithTheFirst() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte, volumeName: Volume);
        var page = _explore.Page();
        page.SelectedColouring = ExploreColouring.Growth;

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(("Logs", 10), ("Old", 30))));

        Assert.True(page.ShowsGrowth);
        Assert.Null(page.Growth.Comparison);
        Assert.Equal(GrowthText.NothingEarlier, page.Growth.Note);
        Assert.Empty(page.Growth.Rows);
        Assert.Single(page.Growth.UsedSpace);

        _explore.Time.Advance(TimeSpan.FromDays(7));
        var second = Drive(("Logs", 250));
        await _explore.ScanAsync(page, ExploreScan.Fast(second));

        Assert.NotNull(page.Growth.Comparison);
        Assert.Same(second, page.Growth.Comparison.Tree);
        Assert.NotEmpty(page.Growth.Since);
        Assert.False(page.Growth.HasNote);

        var logs = page.Growth.Rows.First(row => row.Path == @"C:\Logs");
        Assert.Equal("+240 MB", logs.Change);
        Assert.Equal("Logs", logs.Name);
        Assert.Equal(@"C:\", logs.Parent);

        var old = page.Growth.Rows[^1];
        Assert.Equal(@"C:\Old", old.Path);
        Assert.Equal("Removed, held 30.0 MB", old.Change);
        Assert.False(old.CanOpen);

        Assert.Equal(2, page.Growth.UsedSpace.Count);
        Assert.Equal(60, page.Growth.UsedSpace[^1].Share, precision: 6);
        Assert.Equal(GrowthPalette.Bands.Select(band => band.Label), page.Legend.Select(band => band.Label));
    });

    /// <summary>A walk compared with a file-table scan says, in the panel, that the comparison is approximate.</summary>
    [Fact]
    public void AComparisonAcrossRoutesSaysItIsApproximate() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte, volumeName: Volume);
        var page = _explore.Page();

        await _explore.ScanAsync(page, ExploreScan.Walked(Drive(("Logs", 10)), Core.Scanning.FallbackReason.NotElevated));
        _explore.Time.Advance(TimeSpan.FromDays(1));
        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(("Logs", 20))));

        Assert.Equal(GrowthText.Approximate(GrowthApproximation.RouteChanged), page.Growth.Note);
    });

    /// <summary>A folder's scan is neither kept nor compared, and the panel says why.</summary>
    [Fact]
    public void AScanOfAFolderIsNotCompared() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte, volumeName: Volume);
        var page = _explore.Page();
        page.ScopeTo(@"C:\Users\testuser");

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(("Logs", 10))));

        Assert.Null(page.Growth.Comparison);
        Assert.Equal(GrowthText.NotWholeDrive, page.Growth.Note);
        Assert.Empty(_explore.History.Kept(Volume));
    });

    /// <summary>A drive Windows did not name by GUID cannot be told from another one, so its scans are not kept.</summary>
    [Fact]
    public void ADriveWithoutAVolumeNameIsNotKept() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte);
        var page = _explore.Page();

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(("Logs", 10))));

        Assert.Equal(GrowthText.NoVolumeName, page.Growth.Note);
        Assert.Empty(_explore.History.All());
    });

    /// <summary>A row opens its folder, and the readout under the map states the shape's own change.</summary>
    [Fact]
    public void ARowOpensItsFolderAndTheReadoutStatesTheChange() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte, volumeName: Volume);
        var page = _explore.Page();
        page.SelectedColouring = ExploreColouring.Growth;

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(("Logs", 10))));
        _explore.Time.Advance(TimeSpan.FromDays(1));
        var second = Drive(("Logs", 250));
        await _explore.ScanAsync(page, ExploreScan.Fast(second));

        var logs = page.Growth.Rows.First(row => row.Path == @"C:\Logs");
        page.ShowFolder(logs);

        Assert.Equal(logs.Node, page.CurrentNode);

        page.Hover(new ExploreHit(logs.Node, second.SizeOf(logs.Node)));

        Assert.EndsWith(", +240 MB since the last scan", page.HoveredFigures);
    });

    /// <summary>
    /// Removing the summary a comparison is against, in Settings, stops the comparison on screen: the
    /// user asked for it to be gone.
    /// </summary>
    [Fact]
    public void RemovingTheComparedSummaryStopsTheComparison() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte, volumeName: Volume);
        var page = _explore.Page();

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(("Logs", 10))));
        _explore.Time.Advance(TimeSpan.FromDays(1));
        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(("Logs", 250))));

        var redrawn = 0;
        page.ViewChanged += (_, _) => redrawn++;

        var settings = new ScanHistorySettingsViewModel(_explore.History);
        await settings.RefreshAsync(CancellationToken.None);
        settings.Remove(settings.Kept[^1]);

        Assert.Null(page.Growth.Comparison);
        Assert.Equal(GrowthText.NothingEarlier, page.Growth.Note);
        Assert.Empty(page.Growth.Rows);
        Assert.Single(page.Growth.UsedSpace);
        Assert.True(redrawn > 0);
        Assert.False(settings.RemoveFailed);

        settings.RemoveAll();

        Assert.True(settings.HasNoKept);
        Assert.Empty(page.Growth.UsedSpace);
    });

    /// <summary>A new scan starting takes the comparison off screen, because it describes the tree being replaced.</summary>
    [Fact]
    public void AScanStartingClearsTheComparison() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte, volumeName: Volume);
        var page = _explore.Page();

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(("Logs", 10))));
        _explore.Time.Advance(TimeSpan.FromDays(1));
        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(("Logs", 250))));

        var running = page.ScanCommand.ExecuteAsync(null);

        Assert.Null(page.Growth.Comparison);
        Assert.Empty(page.Growth.Rows);

        page.ScanCancelCommand.Execute(null);
        await running;
    });

    /// <summary>A drive of top-level folders, each holding one file of the given number of megabytes.</summary>
    private static ExploreTree Drive(params (string Name, long Megabytes)[] folders)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var first = builder.AddChildren(ExploreTreeBuilder.RootNode, [.. folders.Select(folder => ExploreFixture.Folder(folder.Name))]);

        for (var i = 0; i < folders.Length; i++)
        {
            builder.AddChildren(first + i, [ExploreFixture.File("data.bin", folders[i].Megabytes * Megabyte)]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
