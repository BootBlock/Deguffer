using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Links;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page wires the broken links (#727): the Files layout's listing of them below the
/// folder on screen, and the panel of the Start menu, desktop and Startup folders beside it, with its
/// removal by path. What is listed, and which target is gone, is asserted in Core by
/// <c>BrokenLinksTests</c>, <c>LinkTargetsTests</c> and <c>ShortcutFolderReportTests</c>.
/// </summary>
public sealed class ExploreBrokenLinksTests : IDisposable
{
    /// <summary>The drive every target here is on: a fixed disk the fakes describe, and never read.</summary>
    private const string Drive = @"Q:\";

    private readonly ExploreFixture _explore = new();

    public ExploreBrokenLinksTests() => _explore.Volumes.With(Drive);

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// The List box turns the Files layout to the links whose target is gone, each stating where it
    /// points, and a link to a drive that is not there is counted and never listed. Turned back, the
    /// layout lists the largest files again.
    /// </summary>
    [Fact]
    public void TheFilesLayoutListsTheBrokenLinksBelowTheFolderOnScreen() => UiThread.Run(async () =>
    {
        var (page, root) = await ScannedAsync();
        page.SelectedView = ExploreView.Files;
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 3, "every file listed");

        page.Files.ListingIndex = 1;

        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 1 && page.Files.Rows[0].IsLink, "the broken link listed");
        var row = page.Files.Rows[0];
        Assert.Equal(Path.Combine(root, "Desktop", "Old Game.lnk"), row.Path);
        Assert.Equal($@"Points at {Drive}Games\Old\game.exe, which is not there.", row.TargetLine);
        Assert.Equal(
            "1 shortcut or link here leads nowhere. 1 more leads to a drive or share that is not connected or could not be checked, so it is not listed.",
            page.Files.Summary);

        page.Files.ListingIndex = 0;

        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 3 && !page.Files.Rows.Any(listed => listed.IsLink), "the files listed again");
    });

    /// <summary>
    /// The panel opens beside the broken links only, reads the shortcut folders as it opens, and lists
    /// what leads nowhere there; the panel of what grew gives it the side of the page.
    /// </summary>
    [Fact]
    public void ThePanelOfShortcutFoldersOpensBesideTheBrokenLinksAndReadsTheFolders() => UiThread.Run(async () =>
    {
        var startMenu = StartMenu(out var gone, out _);
        var (page, _) = await ScannedAsync();
        page.SelectedColouring = ExploreColouring.Growth;
        page.SelectedView = ExploreView.Files;

        Assert.False(page.ShowsShortcutFolders);
        Assert.False(page.ShortcutFolders.IsActive);

        page.Files.ListingIndex = 1;

        Assert.True(page.ShowsShortcutFolders);
        Assert.False(page.ShowsGrowth);
        await Eventually.HoldsAsync(() => page.ShortcutFolders.Rows.Count == 1, "the folder's broken shortcut listed");
        Assert.Equal(gone, page.ShortcutFolders.Rows[0].Path);
        Assert.StartsWith("1 shortcut or link there leads nowhere. 1 more leads to a drive", page.ShortcutFolders.Summary);
        Assert.True(Directory.Exists(startMenu));

        page.SelectedView = ExploreView.List;

        Assert.False(page.ShowsShortcutFolders);
        Assert.False(page.ShortcutFolders.IsActive);
        Assert.True(page.ShowsGrowth);
    });

    /// <summary>
    /// A row's button removes that shortcut and nothing else, to the Recycle Bin, after the question,
    /// and the panel reads the folders again. The shortcut beside it, whose target is on a drive that is
    /// not there, survives (§5.6).
    /// </summary>
    [Fact]
    public void ARowsButtonMovesThatShortcutToTheRecycleBin() => UiThread.Run(async () =>
    {
        StartMenu(out var gone, out var away);
        var page = await PanelAsync();

        await page.ShortcutFolders.RemoveCommand.ExecuteAsync(page.ShortcutFolders.Rows[0]);

        Assert.Equal([gone], _explore.Bin.Paths);
        Assert.Single(_explore.Prompt.Asked);
        Assert.False(File.Exists(gone));
        Assert.True(File.Exists(away));
        Assert.Empty(page.ShortcutFolders.Rows);
        Assert.Empty(page.Status.Checks);
    });

    /// <summary>
    /// The target is asked about again after the user agrees: a program reinstalled meanwhile has put
    /// it back, and the shortcut is left where it is with the reason, never moved to the bin.
    /// </summary>
    [Fact]
    public void AShortcutWhoseTargetCameBackIsLeftWhereItIs() => UiThread.Run(async () =>
    {
        StartMenu(out var gone, out _);
        var page = await PanelAsync();
        var answer = new TaskCompletionSource<bool>();
        _explore.Prompt = new FakeExploreConfirmation(answer.Task);

        var removing = page.ShortcutFolders.RemoveCommand.ExecuteAsync(page.ShortcutFolders.Rows[0]);
        await _explore.Prompt.FirstAsked;

        // While the question is open: the reinstall the look again has to see.
        _explore.Probe.File($@"{Drive}Games\Old\game.exe");
        answer.SetResult(true);
        await removing;

        Assert.Empty(_explore.Bin.Paths);
        Assert.True(File.Exists(gone));
        Assert.Equal(ExploreShortcutFolders.Withdrawn, page.Status.Sentence);
    });

    /// <summary>
    /// A shortcut the panel removes from a folder the scan on screen holds is drawn out of the tree, as
    /// one removed from the map is, so no row or shape stands for what is gone.
    /// </summary>
    [Fact]
    public void AShortcutRemovedFromTheScannedFolderLeavesTheTreeOnScreen() => UiThread.Run(async () =>
    {
        var (page, root) = await ScannedAsync();
        var shortcut = Path.Combine(root, "Desktop", "Old Game.lnk");
        _explore.ShortcutFolders.Add(Path.Combine(root, "Desktop"));
        page.SelectedView = ExploreView.Files;
        page.Files.ListingIndex = 1;
        await Eventually.HoldsAsync(() => page.ShortcutFolders.Rows.Count == 1, "the scanned shortcut listed");
        Assert.NotNull(ExploreTreePaths.TryNodeAt(page.Tree!, shortcut));

        await page.ShortcutFolders.RemoveCommand.ExecuteAsync(page.ShortcutFolders.Rows[0]);

        Assert.Null(ExploreTreePaths.TryNodeAt(page.Tree!, shortcut));
        await Eventually.HoldsAsync(() => page.Files.Rows.Count == 0, "the removed link no longer listed");
    });

    /// <summary>A page on the Files layout's broken links, its panel read.</summary>
    private async Task<ExploreViewModel> PanelAsync()
    {
        var (page, _) = await ScannedAsync();
        page.SelectedView = ExploreView.Files;
        page.Files.ListingIndex = 1;
        await Eventually.HoldsAsync(() => page.ShortcutFolders.Rows.Count == 1, "the panel read");

        return page;
    }

    /// <summary>
    /// A Start menu folder holding a shortcut whose target is gone and one whose target is on a drive
    /// that is not there, named to the page.
    /// </summary>
    private string StartMenu(out string gone, out string away)
    {
        var folder = _explore.Temp.CreateDirectory("Start Menu");
        gone = LongPath.Display(_explore.Temp.CreateFile(1_200, "Start Menu", "Programs", "Old Game.lnk"));
        away = LongPath.Display(_explore.Temp.CreateFile(1_100, "Start Menu", "Away.lnk"));
        _explore.Links.With(gone, LinkKind.Shortcut, $@"{Drive}Games\Old\game.exe");
        _explore.Links.With(away, LinkKind.Shortcut, @"E:\Games\game.exe");
        _explore.ShortcutFolders.Add(folder);

        return folder;
    }

    /// <summary>
    /// A scanned folder on the disk:
    /// <code>
    /// scan
    ///   Desktop
    ///     Old Game.lnk   its target gone
    ///     Away.lnk       its target on a drive that is not there
    ///   big.bin
    /// </code>
    /// </summary>
    private async Task<(ExploreViewModel Page, string Root)> ScannedAsync()
    {
        var root = _explore.Temp.CreateDirectory("scan");
        var gone = _explore.Temp.CreateFile(1_200, "scan", "Desktop", "Old Game.lnk");
        var away = _explore.Temp.CreateFile(1_100, "scan", "Desktop", "Away.lnk");
        _explore.Temp.CreateFile(4_000, "scan", "big.bin");
        _explore.Links.With(gone, LinkKind.Shortcut, $@"{Drive}Games\Old\game.exe");
        _explore.Links.With(away, LinkKind.Shortcut, @"E:\Games\game.exe");

        var builder = new ExploreTreeBuilder(root);
        var desktop = builder.AddChildren(
            ExploreTreeBuilder.RootNode, [ExploreFixture.Folder("Desktop"), ExploreFixture.File("big.bin", 4_000)]);
        builder.AddChildren(desktop, [ExploreFixture.File("Old Game.lnk", 1_200), ExploreFixture.File("Away.lnk", 1_100)]);

        var page = _explore.Page();
        page.Targeting.ScopeTo(root);
        await _explore.ScanAsync(page, ExploreScan.Fast(builder.Build(ExploreChildOrder.BySize)));

        return (page, root);
    }
}
