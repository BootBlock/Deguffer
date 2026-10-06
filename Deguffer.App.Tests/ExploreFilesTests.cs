using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Scanning;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page wires the Files layout: when it searches, what reaches its rows, how a
/// refusal is stated on one, and that nothing it lists is picked or acted on by anything but a
/// gesture. What is listed, and in what order, is asserted in Core by <c>LargestFilesTests</c>.
/// </summary>
public sealed class ExploreFilesTests : IDisposable
{
    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// The largest files at every depth below the folder on screen, and no folder among them. The
    /// search waits until the layout is on screen, because it is a pass over every file below.
    /// </summary>
    [Fact]
    public void TheLargestFilesAtEveryDepthAreListedOnceTheLayoutIsOnScreen() => UiThread.Run(async () =>
    {
        var (page, tree) = await ScannedAsync();

        // Long enough for a search started by the scan to have finished and been shown, had one been.
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.Empty(page.Files.Rows);
        Assert.Equal(string.Empty, page.Files.Summary);

        page.SelectedView = ExploreView.Files;

        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 4, "every file listed");

        Assert.Equal(["game.iso", "setup.exe", "notes.txt", "keep.bin"], page.Files.Rows.Select(row => row.Name));
        Assert.All(page.Files.Rows, row => Assert.False(tree.IsDirectory(row.Node)));
        Assert.Equal(tree.PathOf(Find(tree, "game.iso")), page.Files.Rows[0].Path);
        Assert.Equal(tree.PathOf(tree.ParentOf(Find(tree, "game.iso"))), page.Files.Rows[0].Folder);
        Assert.Equal("4 files here, largest first.", page.Files.Summary);
        Assert.True(page.ShowsFileFilters);
        Assert.False(page.ShowsLegend);
    });

    /// <summary>The list follows the folder the reader opens, and lists nothing outside it.</summary>
    [Fact]
    public void TheListFollowsTheFolderOnScreen() => UiThread.Run(async () =>
    {
        var (page, tree) = await ScannedAsync();
        page.SelectedView = ExploreView.Files;
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 4, "every file listed");

        page.Descend(Find(tree, "games"));

        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 2, "the folder's files listed");
        Assert.Equal(["game.iso", "setup.exe"], page.Files.Rows.Select(row => row.Name));
    });

    /// <summary>
    /// Each box narrows the list, and they apply together. The age is measured from the page's clock,
    /// so the test's own instant decides it rather than the machine's.
    /// </summary>
    [Fact]
    public void TheFourBoxesNarrowTheListTogether() => UiThread.Run(async () =>
    {
        var (page, _) = await ScannedAsync();
        page.SelectedView = ExploreView.Files;
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 4, "every file listed");

        page.Files.NameFilter = "*.iso";
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 1, "the name applied");
        Assert.Equal("game.iso", page.Files.Rows[0].Name);
        Assert.Equal("1 file matches.", page.Files.Summary);

        page.Files.NameFilter = string.Empty;
        page.Files.TypeIndex = 1 + (int)FileCategory.Installers;
        await Eventually.HoldsAsync(() => page.Files.Rows is [{ Name: "setup.exe" }], "the type applied");

        page.Files.TypeIndex = 0;
        page.Files.AgeIndex = (int)FileAge.OneYear;
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 2, "the age applied");
        Assert.Equal(["game.iso", "setup.exe"], page.Files.Rows.Select(row => row.Name));

        page.Files.NameFilter = "game";
        await Eventually.HoldsAsync(() => page.Files.Rows is [{ Name: "game.iso" }], "the name and the age applied");

        page.Files.NameFilter = "nothing like it";
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 0, "nothing matched");
        Assert.Equal("No file here matches.", page.Files.Summary);
    });

    /// <summary>
    /// §7.1: a filter narrows what is listed, never what is picked. Rows arriving select nothing, and
    /// nothing the layout does on its own puts anything into the selection.
    /// </summary>
    [Fact]
    public void NothingListedIsPickedUntilARowIsChosen() => UiThread.Run(async () =>
    {
        var (page, tree) = await ScannedAsync();
        page.SelectedView = ExploreView.Files;
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 4, "every file listed");

        page.Files.NameFilter = "o";
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 2, "the name applied");

        Assert.Empty(page.Selection.Nodes);

        page.Selection.Select([page.Files.Rows[0].Node]);

        Assert.Equal($"Selected: {tree.PathOf(Find(tree, "game.iso"))}", page.Selection.Label);
    });

    /// <summary>
    /// A file Explore will not remove is listed with the reason on its row, and picking it and asking
    /// for a removal removes nothing. §5.6's negative holds: the refused file, and the file beside it
    /// that nobody picked, are both still on the disk.
    /// </summary>
    [Fact]
    public void ARefusedFileIsListedWithItsRefusalAndCannotBeRemoved() => UiThread.Run(async () =>
    {
        var (page, tree) = await ScannedAsync(refusing: "notes.txt");
        page.SelectedView = ExploreView.Files;
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 4, "every file listed");

        var refused = Assert.Single(page.Files.Rows, row => row.Name == "notes.txt");

        Assert.Equal("Kept by this test.", refused.Refusal);
        Assert.True(refused.IsRefused);
        Assert.Contains("Explore will not remove this: Kept by this test.", refused.Description);
        Assert.All(page.Files.Rows.Where(row => row != refused), row => Assert.Null(row.Refusal));

        page.Selection.Select([refused.Node]);

        Assert.Equal("Kept by this test.", page.Selection.Note);

        await page.Selection.DeleteCommand.ExecuteAsync(null);

        Assert.True(File.Exists(refused.Path));
        Assert.True(File.Exists(tree.PathOf(Find(tree, "keep.bin"))));
        Assert.Contains(page.Files.Rows, row => row.Node == refused.Node);
    });

    /// <summary>
    /// Until the policy is built, every path is refused with a sentence saying why. The rows say the
    /// same, and are told what the answer turned out to be once it lands.
    /// </summary>
    [Fact]
    public void TheRefusalsAreRestatedWhenThePolicyLands() => UiThread.Run(async () =>
    {
        var building = new TaskCompletionSource<ExploreActionPolicy>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explore.Build = _ => building.Task;

        var (page, _) = await ScannedAsync();
        page.SelectedView = ExploreView.Files;
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 4, "every file listed");

        Assert.All(page.Files.Rows, row => Assert.Contains("still working out what it has to protect", row.Refusal));

        await Task.Run(() => building.SetResult(_explore.Policy()));

        await Eventually.HoldsAsync(() => page.Files.Rows.All(row => row.Refusal is null), "the refusals restated");
    });

    /// <summary>
    /// A file removed from the list leaves it, as it leaves the folder list, and the files nobody
    /// picked stay listed and on the disk.
    /// </summary>
    [Fact]
    public void ARemovedFileLeavesTheList() => UiThread.Run(async () =>
    {
        var (page, tree) = await ScannedAsync();
        page.SelectedView = ExploreView.Files;
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 4, "every file listed");

        var removed = Assert.Single(page.Files.Rows, row => row.Name == "setup.exe");
        page.Selection.Select([removed.Node]);

        await page.Selection.DeleteCommand.ExecuteAsync(null);

        Assert.False(File.Exists(removed.Path));
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 3, "the removed file gone from the list");
        Assert.DoesNotContain(page.Files.Rows, row => row.Node == removed.Node);
        Assert.True(File.Exists(tree.PathOf(Find(tree, "game.iso"))));
        Assert.True(File.Exists(tree.PathOf(Find(tree, "keep.bin"))));
    });

    /// <summary>
    /// The layout stops searching once it is off screen, and a new tree takes away what it listed
    /// from the last one, whose node numbers mean something else.
    /// </summary>
    [Fact]
    public void LeavingTheLayoutStopsItSearchingAndAnotherTreeClearsIt() => UiThread.Run(async () =>
    {
        var (page, _) = await ScannedAsync();
        page.SelectedView = ExploreView.Files;
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 4, "every file listed");

        page.SelectedView = ExploreView.List;
        Assert.False(page.Files.IsActive);
        Assert.False(page.ShowsFileFilters);

        var (rescan, _) = Disk();
        await _explore.ScanAsync(page, ExploreScan.Fast(rescan));

        Assert.Empty(page.Files.Rows);
    });

    /// <summary>
    /// A scanned folder on the disk, so a removal has something to remove:
    /// <code>
    /// scan
    ///   games
    ///     old
    ///       game.iso   400, three years ago
    ///     setup.exe    300, two years ago
    ///   notes.txt      200, today
    ///   keep.bin       100, today
    /// </code>
    /// </summary>
    private async Task<(ExploreViewModel Page, ExploreTree Tree)> ScannedAsync(string? refusing = null)
    {
        var (tree, root) = Disk();

        if (refusing is not null)
        {
            var path = Path.Combine(root, refusing);
            _explore.Build = _ => Task.FromResult(_explore.Policy("Kept by this test.", path));
        }

        var page = _explore.Page();

        page.ScopeTo(root);
        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        return (page, tree);
    }

    private (ExploreTree Tree, string Root) Disk()
    {
        var root = _explore.Temp.CreateDirectory("scan");
        _explore.Temp.CreateFile(400, "scan", "games", "old", "game.iso");
        _explore.Temp.CreateFile(300, "scan", "games", "setup.exe");
        _explore.Temp.CreateFile(200, "scan", "notes.txt");
        _explore.Temp.CreateFile(100, "scan", "keep.bin");

        var now = _explore.Time.GetUtcNow().UtcDateTime;
        var builder = new ExploreTreeBuilder(root);

        var games = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [ExploreFixture.Folder("games"), Written("notes.txt", 200, now), Written("keep.bin", 100, now)]);

        var old = builder.AddChildren(games, [ExploreFixture.Folder("old"), Written("setup.exe", 300, now.AddYears(-2))]);
        builder.AddChildren(old, [Written("game.iso", 400, now.AddYears(-3))]);

        return (builder.Build(ExploreChildOrder.BySize), root);
    }

    private static ExploreChild Written(string name, long size, DateTime when) =>
        new(name, IsDirectory: false, IsLink: false, size, ExploreTimestamp.FromUtc(when), ExploreTimestamp.FromUtc(when));

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

        throw new InvalidOperationException($"Nothing named {name}.");
    }
}
