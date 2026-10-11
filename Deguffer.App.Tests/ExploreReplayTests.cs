using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.History;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page wires the replay of what grew (#285): when it is offered, what the scrubber
/// hands the map, how a play moves, and that the selection does nothing while a step is on screen.
/// What each step holds is Core's, and tested there.
/// </summary>
public sealed class ExploreReplayTests : IDisposable
{
    private const string Volume = @"\\?\Volume{0b6f5c1e-3a7d-4c2b-9e10-6d2f1a4b8c33}\";

    private const long Megabyte = 1024L * 1024;

    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// A drive compared with an earlier scan offers the replay while the map is coloured by growth.
    /// The scrubber starts at now, where the map is the drive, and anywhere else hands the map a step
    /// of the replay of the folder on screen.
    /// </summary>
    [Fact]
    public void TheScrubberHandsTheMapAStepOfTheFolderOnScreen() => UiThread.Run(async () =>
    {
        var page = await ComparedAsync();
        var redraws = 0;
        page.ViewChanged += (_, _) => redraws++;

        Assert.True(page.Replay.IsOffered);
        Assert.True(page.Replay.CanReplay);
        Assert.Equal(GrowthText.ReplayEstimate, page.Replay.Note);
        Assert.Null(page.Replay.Frame);

        page.Replay.Step = 3;

        var frame = page.Replay.Frame!;
        Assert.Equal(3, frame.Step);
        Assert.Same(page.Tree, frame.Replay.Today);
        Assert.True(page.Replay.Shows);
        Assert.Equal(1, redraws);

        page.Replay.Step = page.Replay.Steps;

        Assert.Null(page.Replay.Frame);
        Assert.Equal(2, redraws);
    });

    /// <summary>
    /// §7.1: while a step is on screen the selection does nothing, because the map is a picture of
    /// what the drive held and does not outline what is picked. Back at now it acts again.
    /// </summary>
    [Fact]
    public void TheSelectionDoesNothingWhileAStepIsOnScreen() => UiThread.Run(async () =>
    {
        var page = await ComparedAsync();
        var logs = Child(page.Tree!, "Logs");
        page.Selection.Select([logs]);

        Assert.True(page.Selection.CanAct);
        Assert.True(page.Selection.DeleteCommand.CanExecute(null));

        page.Replay.Step = 0;

        Assert.False(page.Selection.CanAct);
        Assert.False(page.Selection.DeleteCommand.CanExecute(null));
        Assert.Equal([logs], page.Selection.Nodes);

        page.Replay.Step = page.Replay.Steps;

        Assert.True(page.Selection.CanAct);
        Assert.True(page.Selection.DeleteCommand.CanExecute(null));
    });

    /// <summary>
    /// The replay is of the folder on screen and the colours asked for, so another folder, or colours
    /// that are not growth, put it back to now, where the map is the drive and the selection acts.
    /// </summary>
    [Fact]
    public void AnotherFolderOrOtherColoursPutTheReplayBackToNow() => UiThread.Run(async () =>
    {
        var page = await ComparedAsync();

        page.Replay.Step = 0;
        page.Descend(Child(page.Tree!, "Logs"));

        Assert.Null(page.Replay.Frame);
        Assert.Equal(page.Replay.Steps, page.Replay.Step);
        Assert.True(page.Replay.CanReplay);
        Assert.True(page.Selection.CanAct);

        page.Replay.Step = 0;
        page.SelectedColouring = ExploreColouring.Branch;

        Assert.Null(page.Replay.Frame);
        Assert.False(page.Replay.IsOffered);
        Assert.True(page.Selection.CanAct);
    });

    /// <summary>A folder the earlier scan did not record has no earlier picture, says so, and cannot be played.</summary>
    [Fact]
    public void AFolderTheEarlierScanDidNotRecordIsNotReplayed() => UiThread.Run(async () =>
    {
        var page = await ComparedAsync(("Logs", 250), ("New", 40));

        page.Descend(Child(page.Tree!, "New"));

        Assert.True(page.Replay.IsOffered);
        Assert.False(page.Replay.CanReplay);
        Assert.False(page.Replay.PlayCommand.CanExecute(null));
        Assert.Equal(GrowthText.ReplayNotRecorded, page.Replay.Note);

        page.Replay.Step = 0;

        Assert.Null(page.Replay.Frame);
        Assert.True(page.Selection.CanAct);
    });

    /// <summary>
    /// A play starts from the earlier scan and moves a step at a time to now, where it stops. The
    /// reader moving the scrubber stops it where they put it, and a second play goes on from there.
    /// </summary>
    [Fact]
    public void APlayMovesAStepAtATimeToNow() => UiThread.Run(async () =>
    {
        var replay = await ReplayAsync(animationsEnabled: true);

        replay.PlayCommand.Execute(null);

        Assert.True(replay.IsPlaying);
        Assert.Equal(0, replay.Step);
        Assert.Equal("Pause", replay.PlayText);

        Assert.True(replay.Advance());
        Assert.Equal(1, replay.Step);

        replay.Step = 5;

        Assert.False(replay.IsPlaying);
        Assert.False(replay.Advance());
        Assert.Equal(5, replay.Step);

        replay.PlayCommand.Execute(null);

        Assert.Equal(5, replay.Step);

        while (replay.Advance())
        {
        }

        Assert.Equal(replay.Steps, replay.Step);
        Assert.False(replay.IsPlaying);
        Assert.Null(replay.Frame);
    });

    /// <summary>
    /// With animation effects off the scrubber has only its two ends, and a play shows the earlier
    /// scan and then the drive now, with nothing estimated between them (#273).
    /// </summary>
    [Fact]
    public void WithAnimationEffectsOffAPlayShowsOnlyTheTwoEnds() => UiThread.Run(async () =>
    {
        var replay = await ReplayAsync(animationsEnabled: false);

        Assert.Equal(replay.Steps, replay.StepFrequency);

        replay.PlayCommand.Execute(null);

        Assert.Equal(0, replay.Step);
        Assert.False(replay.Advance());
        Assert.Equal(replay.Steps, replay.Step);
        Assert.Equal(GrowthReplay.Pace(Core.Viewing.MotionToken.Shift.For(false)), replay.Pace);
    });

    /// <summary>A page compared with an earlier scan of the drive, coloured by growth, on the root.</summary>
    private async Task<ExploreViewModel> ComparedAsync(params (string Name, long Megabytes)[] now)
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte, volumeName: Volume);
        var page = _explore.Page();
        page.SelectedColouring = ExploreColouring.Growth;

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(("Logs", 10), ("Old", 30))));
        _explore.Time.Advance(TimeSpan.FromDays(7));
        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(now.Length > 0 ? now : [("Logs", 250)])));

        Assert.NotNull(page.Growth.Comparison);

        return page;
    }

    /// <summary>A replay of the page's comparison on its root, for a reader with animation effects on or off.</summary>
    private async Task<ExploreReplay> ReplayAsync(bool animationsEnabled)
    {
        var page = await ComparedAsync();
        var replay = new ExploreReplay(new ScriptedMotion(animationsEnabled));
        replay.Follow(page.Growth.Comparison, page.Tree, page.CurrentNode);

        return replay;
    }

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

    private static int Child(ExploreTree tree, string name)
    {
        foreach (var child in tree.ChildrenOf(tree.RootNode))
        {
            if (tree.NameOf(child) == name)
            {
                return child;
            }
        }

        throw new InvalidOperationException($"No child called {name}.");
    }
}
