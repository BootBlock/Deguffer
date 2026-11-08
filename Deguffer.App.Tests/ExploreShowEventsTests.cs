using Deguffer.App.Diagnostics;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page marks each tree it shows for a trace (<see cref="ShellEvents"/>): one start and
/// one stop with the cause, around the selection, the rows and the hand-off to the map, and nothing
/// that names what is in the tree.
/// </summary>
public sealed class ExploreShowEventsTests : IDisposable
{
    /// <summary>One show, from its start to its stop, its parts nested inside it in the order they run.</summary>
    private static readonly string[] OneShow =
    [
        "ShowStart",
        "SelectionStart", "SelectionStop",
        "RowsStart", "RowsStop",
        "HandoffStart", "HandoffStop",
        "ShowStop",
    ];

    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// A step into a folder is one show with the cause <see cref="ShowCause.Step"/>: the start gives the
    /// tree's node total, the stop and the rows give the rows listed, and no payload is a string, so
    /// none of the names in the tree reaches the trace.
    /// </summary>
    [Fact]
    public void AStepIsOneShowWithItsCauseAndNoNameFromTheTree() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Listed();
        var tree = Drive();

        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        var users = tree.ChildrenOf(tree.RootNode)[0];
        using var recorder = new ShellEventRecorder();

        page.Descend(users);

        Assert.Equal(OneShow, recorder.Names);

        var rows = page.Listing.Rows.Count;

        Assert.NotEqual(0, rows);
        Assert.Equal([(int)ShowCause.Step, tree.NodeCount], recorder.Events[0].Numbers);
        Assert.Equal([rows], recorder.Events[4].Numbers);
        Assert.Equal([(int)ShowCause.Step, rows], recorder.Events[^1].Numbers);
        Assert.DoesNotContain(recorder.Events.SelectMany(raised => raised.Payload), value => value is string);
    });

    /// <summary>
    /// Under a map a step lists no rows, and its show says so rather than counting the rows the list
    /// last held for another folder.
    /// </summary>
    [Fact]
    public void AStepUnderAMapListsNoRows() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Listed();
        var tree = Drive();

        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        Assert.NotEmpty(page.Listing.Rows);

        page.SelectedView = ExploreView.Treemap;
        using var recorder = new ShellEventRecorder();

        page.Descend(tree.ChildrenOf(tree.RootNode)[0]);

        Assert.Equal(OneShow, recorder.Names);
        Assert.Equal([0], recorder.Events[4].Numbers);
        Assert.Equal([(int)ShowCause.Step, 0], recorder.Events[^1].Numbers);
    });

    /// <summary>
    /// A scan's snapshot and its finished tree are each one show, with their own causes, in the order
    /// the scan handed them over.
    /// </summary>
    [Fact]
    public void ASnapshotAndTheFinishedTreeAreShowsWithTheirOwnCauses() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var tree = Drive();
        using var recorder = new ShellEventRecorder();

        var running = page.ScanCommand.ExecuteAsync(null);

        _explore.Scanner.Current.Report(new ExploreProgress(2, null, 0, tree));
        await Task.Yield();

        _explore.Scanner.Current.Finish(ExploreScan.Fast(tree));
        await running;

        Assert.Equal([(int)ShowCause.Snapshot, (int)ShowCause.Finished], Causes(recorder, "ShowStart"));
        Assert.Equal([(int)ShowCause.Snapshot, (int)ShowCause.Finished], Causes(recorder, "ShowStop"));
    });

    /// <summary>What is left after a removal is one show with the cause <see cref="ShowCause.Removal"/>.</summary>
    [Fact]
    public void WhatIsLeftAfterARemovalIsAShowOfTheRemoval() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(Path.GetPathRoot(_explore.Temp.Path)!);
        var page = _explore.Page();
        var (tree, folder) = _explore.OnDisk();

        page.Targeting.ScopeTo(tree.PathOf(tree.RootNode));
        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        page.Selection.Select([folder]);
        using var recorder = new ShellEventRecorder();

        await page.Selection.DeleteCommand.ExecuteAsync(null);

        Assert.False(page.Tree!.Holds(folder));
        Assert.Equal([(int)ShowCause.Removal], Causes(recorder, "ShowStart"));
        Assert.Equal([(int)ShowCause.Removal], Causes(recorder, "ShowStop"));
    });

    private static IEnumerable<int> Causes(ShellEventRecorder recorder, string name) =>
        recorder.Events.Where(raised => raised.Name == name).Select(raised => raised.Numbers.First());

    /// <summary>Users holding a profile holding a file, beside a file.</summary>
    private static ExploreTree Drive()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var users = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Folder("Users"),
            ExploreFixture.File("pagefile.sys", 100),
        ]);
        var profile = builder.AddChildren(users, [ExploreFixture.Folder("testuser")]);

        builder.AddChildren(profile, [ExploreFixture.File("ntuser.dat", 1_000)]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
