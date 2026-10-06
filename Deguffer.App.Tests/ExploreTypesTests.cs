using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page wires the breakdown by kind of file and the map coloured by type: when each is
/// measured, what reaches the panel and the line under the map, and that choosing a kind lists its
/// files. What each kind holds, and which kind a folder is painted, is asserted in Core by
/// <c>TypeBreakdownTests</c> and <c>DominantTypesTests</c>.
/// </summary>
public sealed class ExploreTypesTests : IDisposable
{
    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// The panel and its legend are on screen only while the colours are types and something has been
    /// scanned, and nothing is measured before that: each measurement is a pass over the folder.
    /// </summary>
    [Fact]
    public void TheBreakdownIsMeasuredAndShownOnlyWhileTheColoursAreTypes() => UiThread.Run(async () =>
    {
        var (page, _) = await ScannedAsync();

        // Another colouring chosen, as well as the one the page starts with.
        page.SelectedColouring = ExploreColouring.Growth;

        // Long enough for a measurement started by the scan to have been shown, had one been started.
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.False(page.ShowsTypes);
        Assert.Empty(page.Types.Rows);
        Assert.Null(page.Types.Dominant);

        page.SelectedColouring = ExploreColouring.Type;

        await Eventually.HoldsAsync(() => page.Types.Rows.Count == 3, "the breakdown shown");

        Assert.True(page.ShowsTypes);
        Assert.True(page.ShowsLegend);
        Assert.Equal(TypePalette.Bands.Select(band => band.Label), page.Legend.Select(band => band.Label));
        Assert.Equal(
            [FileCategory.Video, FileCategory.DiskImages, FileCategory.Documents],
            page.Types.Rows.Select(row => row.Category));
        Assert.Equal("Video", page.Types.Rows[0].Label);
        Assert.Equal("2 files", page.Types.Rows[0].Files);
        Assert.Equal($".mp4 {FreeSpace.Format(4_000)}, .mkv {FreeSpace.Format(3_000)}", page.Types.Rows[0].Extensions);
        Assert.Equal($"{FreeSpace.Format(12_010)} in 4 files.", page.Types.Summary);
        Assert.Equal(TypePalette.For(FileCategory.Video), Colour(page.Types.Rows[0]));

        page.SelectedColouring = ExploreColouring.Branch;

        Assert.False(page.ShowsTypes);
    });

    /// <summary>The breakdown follows the folder the reader opens, and counts nothing outside it.</summary>
    [Fact]
    public void TheBreakdownFollowsTheFolderOnScreen() => UiThread.Run(async () =>
    {
        var (page, tree) = await ScannedAsync();
        page.SelectedColouring = ExploreColouring.Type;
        await Eventually.HoldsAsync(() => page.Types.Rows.Count == 3, "the breakdown shown");

        page.Descend(Find(tree, "Images"));

        await Eventually.HoldsAsync(
            () => page.Types.Rows is [{ Category: FileCategory.DiskImages }], "the folder's breakdown shown");
        Assert.Equal(100, page.Types.Rows[0].Share);
    });

    /// <summary>
    /// A folder's kind is measured once a scan has finished and not from the snapshots of one still
    /// running, which arrive every few hundred milliseconds. Until then the line under the map names the
    /// kind of a file, which its name says, and says nothing about a folder rather than guess. Once it
    /// arrives the map is drawn again, and the folder is named by its largest kind.
    /// </summary>
    [Fact]
    public void AFoldersKindIsMeasuredOnceTheScanFinishesAndTheMapIsDrawnAgain() => UiThread.Run(async () =>
    {
        var root = _explore.Temp.CreateDirectory("scan");
        var page = _explore.Page();
        page.ScopeTo(root);
        page.SelectedColouring = ExploreColouring.Type;

        var builder = Builder(root);

        // A redraw that can see the folders' kinds. The redraw a tree arriving makes comes before they
        // are measured, so only one the measurement itself asks for counts.
        var drawnWithKinds = false;
        page.ViewChanged += (_, _) => drawnWithKinds |= page.Types.Dominant is not null;

        var running = page.ScanCommand.ExecuteAsync(null);
        var scan = _explore.Scanner.Current;
        var snapshot = builder.Build(ExploreChildOrder.ByName);

        scan.Report(new ExploreProgress(6, null, 0, snapshot));
        await Eventually.HoldsAsync(() => page.Types.Rows.Count == 3, "the snapshot broken down");
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.Null(page.Types.Dominant);

        page.Hover(new ExploreHit(Find(snapshot, "Videos"), snapshot.SizeOf(Find(snapshot, "Videos"))));
        Assert.DoesNotContain("kind", page.HoveredFigures);

        page.Hover(new ExploreHit(Find(snapshot, "trip.mp4"), 1));
        Assert.EndsWith(", video", page.HoveredFigures);

        var finished = builder.Build(ExploreChildOrder.BySize);
        scan.Finish(ExploreScan.Fast(finished));
        await running;

        await Eventually.HoldsAsync(() => ReferenceEquals(page.Types.Dominant?.Tree, finished), "the folders measured");

        Assert.True(drawnWithKinds, "The map was not drawn again when the folders' kinds arrived.");

        page.Hover(new ExploreHit(Find(finished, "Videos"), finished.SizeOf(Find(finished, "Videos"))));
        Assert.EndsWith(", largest kind: video", page.HoveredFigures);

        page.SelectedColouring = ExploreColouring.Branch;
        page.Hover(new ExploreHit(Find(finished, "Videos"), finished.SizeOf(Find(finished, "Videos"))));
        Assert.DoesNotContain("kind", page.HoveredFigures);
    });

    /// <summary>
    /// Choosing a kind in the breakdown lists that kind's files below the folder on screen, and only
    /// those. §7.1: nothing is picked by it.
    /// </summary>
    [Fact]
    public void ChoosingAKindListsItsFiles() => UiThread.Run(async () =>
    {
        var (page, _) = await ScannedAsync();
        page.SelectedColouring = ExploreColouring.Type;
        await Eventually.HoldsAsync(() => page.Types.Rows.Count == 3, "the breakdown shown");

        page.Files.ListOnly(page.Types.Rows[0].Category);
        page.SelectedView = ExploreView.Files;

        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 2, "the kind's files listed");

        Assert.Equal(["trip.mp4", "clip.mkv"], page.Files.Rows.Select(row => row.Name));
        Assert.Equal("Video", page.Files.TypeLabels[page.Files.TypeIndex]);
        Assert.Empty(page.Selection.Nodes);
    });

    private async Task<(ExploreViewModel Page, ExploreTree Tree)> ScannedAsync()
    {
        var root = _explore.Temp.CreateDirectory("scan");
        var tree = Builder(root).Build(ExploreChildOrder.BySize);
        var page = _explore.Page();

        page.ScopeTo(root);
        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        return (page, tree);
    }

    /// <summary>Two folders: one of video, and one of a disk image, with a document beside them.</summary>
    private static ExploreTreeBuilder Builder(string root)
    {
        var builder = new ExploreTreeBuilder(root);

        var videos = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [ExploreFixture.Folder("Videos"), ExploreFixture.Folder("Images"), ExploreFixture.File("notes.txt", 10)]);

        builder.AddChildren(videos, [ExploreFixture.File("trip.mp4", 4_000), ExploreFixture.File("clip.mkv", 3_000)]);
        builder.AddChildren(videos + 1, [ExploreFixture.File("game.iso", 5_000)]);

        return builder;
    }

    private static TileColour Colour(ExploreTypeRow row) => new(row.Swatch.R, row.Swatch.G, row.Swatch.B);

    private static int Find(ExploreTree tree, string name)
    {
        var waiting = new Stack<int>([tree.RootNode]);

        while (waiting.TryPop(out var node))
        {
            if (tree.NameOf(node) == name)
            {
                return node;
            }

            foreach (var child in tree.ChildrenOf(node))
            {
                waiting.Push(child);
            }
        }

        throw new InvalidOperationException($"{name} is not in the tree.");
    }
}
