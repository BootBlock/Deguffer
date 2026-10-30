using Deguffer.App.ViewModels;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Exploring;
using Deguffer.Core.Memory;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// What each page's view-model does with what its map tells it, through the one wiring both pages
/// share: a pick is the selection, and a step up from the map goes up one folder or part.
/// </summary>
public sealed class MapNavigatorTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>§7.1: a shape picked on the map is the whole selection, never added to one made before it.</summary>
    [Fact]
    public void APickOnExploresMapIsTheWholeSelection() => UiThread.Run(async () =>
    {
        var (page, tree, folder) = await ScannedAsync();
        IMapNavigator map = page;

        page.Selection.Select([tree.RootNode + 2]);
        map.Pick(folder);

        Assert.Equal([folder], page.Selection.Nodes);
    });

    /// <summary>A click on no shape clears the selection, so Delete is pointed at nothing rather than at what was picked before.</summary>
    [Fact]
    public void APickOffEveryShapeOnExploresMapClearsTheSelection() => UiThread.Run(async () =>
    {
        var (page, _, folder) = await ScannedAsync();
        IMapNavigator map = page;

        page.Selection.Select([folder]);
        map.Pick(null);

        Assert.Empty(page.Selection.Nodes);
    });

    /// <summary>A step up from Explore's map goes up one folder, and does nothing at the top.</summary>
    [Fact]
    public void AStepUpFromExploresMapGoesUpOneFolderAndNoFurtherThanTheTop() => UiThread.Run(async () =>
    {
        var (page, tree, folder) = await ScannedAsync();
        IMapNavigator map = page;
        var steps = 0;
        page.ViewChanged += (_, _) => steps++;

        page.Descend(folder);
        map.Ascend();

        Assert.Equal(tree.RootNode, page.CurrentNode);
        Assert.Equal(2, steps);
        Assert.False(page.AscendCommand.CanExecute(null));

        map.Ascend();

        Assert.Equal(2, steps);
    });

    /// <summary>§7.2: a shape picked on Memory's map is the one thing picked, and a place with no shape is nothing.</summary>
    [Fact]
    public void APickOnMemorysMapIsTheOneThingPicked() => UiThread.Run(async () =>
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider();
        var machine = new MemoryCloseMachine();
        var page = new MemoryViewModel(
            new MemoryFeed(new QueuedMemorySource(Reading()), clock),
            new MemorySelection(
                new MemoryActions(machine.Policy(), machine.Closer(), () => new ScriptedMemoryPrompt(answer: false)),
                new CrashLog(new FakeUserEnvironment(temp.Path))));
        using var leave = new CancellationTokenSource();
        IMapNavigator map = page;

        var shown = new TaskCompletionSource();
        page.ViewChanged += (_, _) => shown.TrySetResult();
        var watching = page.WatchAsync(leave.Token);
        await shown.Task.WaitAsync(Patience);

        var alpha = page.Tree!.Find(new MemoryNodeKey(MemoryPart.Process, 100, 10))!.Value;

        map.Pick(alpha);
        Assert.Equal(alpha, page.Selection.Node);

        map.Pick(null);
        Assert.Null(page.Selection.Node);

        await leave.CancelAsync();
        await watching;
    });

    private async Task<(ExploreViewModel Page, ExploreTree Tree, int Folder)> ScannedAsync()
    {
        var page = _explore.Page();
        var (tree, folder) = _explore.OnDisk();

        page.ScopeTo(tree.PathOf(tree.RootNode));
        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        return (page, tree, folder);
    }

    private static MemorySnapshot Reading() => new MemorySnapshotBuilder()
        .Process(100, 1, "alpha.exe", 320, created: 10)
        .Build();
}
