using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// What is changing the machine on every page. The window and each page's Elevate button read it
/// before ending the process, so an action left recorded after it ended would hold the window open
/// for good, and one dropped early would let the process end under a run.
/// </summary>
public sealed class RunningActionsTests
{
    [Fact]
    public void NothingIsRunningAtFirst()
    {
        var running = new RunningActions();

        Assert.True(running.MayEndProcess);
        Assert.Empty(running.Current);
    }

    [Fact]
    public void AnActionIsRunningUntilItsEndingIsDisposed()
    {
        var running = new RunningActions();

        var clean = running.Begin(RunningAction.StorageClean);

        Assert.False(running.MayEndProcess);
        Assert.Equal([RunningAction.StorageClean], running.Current);

        clean.Dispose();

        Assert.True(running.MayEndProcess);
    }

    /// <summary>One page's action ending says nothing about another page's, which is still running.</summary>
    [Fact]
    public void AnotherPagesActionKeepsRunningWhenOneEnds()
    {
        var running = new RunningActions();

        var clean = running.Begin(RunningAction.StorageClean);
        using var removal = running.Begin(RunningAction.ExploreRemoval);

        clean.Dispose();

        Assert.False(running.MayEndProcess);
        Assert.Equal([RunningAction.ExploreRemoval], running.Current);
    }

    /// <summary>
    /// Two runs of the same kind are two runs. Ending one must leave the other recorded, or the
    /// window would close under the second.
    /// </summary>
    [Fact]
    public void TwoActionsOfOneKindAreCountedApart()
    {
        var running = new RunningActions();

        var first = running.Begin(RunningAction.EntryRemoval);
        using var second = running.Begin(RunningAction.EntryRemoval);

        first.Dispose();

        Assert.Equal([RunningAction.EntryRemoval], running.Current);
    }

    /// <summary>
    /// A second disposal of the same ending must not take another action's record with it: with two
    /// of a kind running, that would end the survivor's record while it still runs.
    /// </summary>
    [Fact]
    public void DisposingAnEndingTwiceEndsOnlyItsOwnAction()
    {
        var running = new RunningActions();

        var first = running.Begin(RunningAction.Uninstall);
        using var second = running.Begin(RunningAction.Uninstall);

        first.Dispose();
        first.Dispose();

        Assert.False(running.MayEndProcess);
    }

    [Fact]
    public void ChangedIsRaisedAsAnActionBeginsAndAsItEnds()
    {
        var running = new RunningActions();
        var seen = new List<bool>();
        running.Changed += (_, _) => seen.Add(running.MayEndProcess);

        running.Begin(RunningAction.BackupRestore).Dispose();

        Assert.Equal([false, true], seen);
    }

    /// <summary>The list handed out is a copy, so a dialog reading it is not changed under it by a run ending.</summary>
    [Fact]
    public void CurrentIsNotChangedByALaterEnding()
    {
        var running = new RunningActions();
        var clean = running.Begin(RunningAction.StorageClean);

        var shown = running.Current;
        clean.Dispose();

        Assert.Equal([RunningAction.StorageClean], shown);
    }

    /// <summary>
    /// The close prompt names what is running, so an action with no name would throw as the window
    /// asked whether it may close, which is the moment a run most needs protecting.
    /// </summary>
    [Fact]
    public void EveryActionHasANameForTheCloseToSay()
    {
        foreach (var action in Enum.GetValues<RunningAction>())
        {
            Assert.False(string.IsNullOrWhiteSpace(RunningActionText.List([action])));
        }
    }
}
