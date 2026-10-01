using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether the window may close, and closing it once a run is over where the user asked for that.
/// Closing the window ends the process, so a close let through while a clean or a removal runs
/// loses that run's §5.6 verification and its report.
/// </summary>
public sealed class CloseGuardTests
{
    private readonly RunningActions _running = new();

    [Fact]
    public void AClosePassesWhenNothingIsRunning()
    {
        Assert.True(new CloseGuard(_running).MayClose);
    }

    [Fact]
    public void ACloseIsHeldWhileAnActionRuns()
    {
        var guard = new CloseGuard(_running);

        using var clean = _running.Begin(RunningAction.StorageClean);

        Assert.False(guard.MayClose);
    }

    [Fact]
    public void ACloseIsHeldWhileAnyActionRunsAfterAnotherEnds()
    {
        var guard = new CloseGuard(_running);

        var clean = _running.Begin(RunningAction.StorageClean);
        using var removal = _running.Begin(RunningAction.ExploreRemoval);
        clean.Dispose();

        Assert.False(guard.MayClose);
    }

    [Fact]
    public void ClosingOnceIdleWaitsForTheLastActionToEnd()
    {
        var guard = new CloseGuard(_running);
        var closes = 0;
        guard.ReadyToClose += (_, _) => closes++;

        var clean = _running.Begin(RunningAction.StorageClean);
        var removal = _running.Begin(RunningAction.ExploreRemoval);

        guard.CloseWhenIdle();
        clean.Dispose();

        Assert.Equal(0, closes);

        removal.Dispose();

        Assert.Equal(1, closes);
    }

    /// <summary>The run can finish while the user reads the question, and the answer still closes the window.</summary>
    [Fact]
    public void ClosingOnceIdleClosesAtOnceWhereTheRunEndedDuringTheQuestion()
    {
        var guard = new CloseGuard(_running);
        var closes = 0;
        guard.ReadyToClose += (_, _) => closes++;

        _running.Begin(RunningAction.StorageClean).Dispose();
        guard.CloseWhenIdle();

        Assert.Equal(1, closes);
    }

    /// <summary>Keeping the window open on a second press withdraws the first press's choice.</summary>
    [Fact]
    public void KeepingTheWindowOpenWithdrawsAnEarlierChoiceToClose()
    {
        var guard = new CloseGuard(_running);
        var closes = 0;
        guard.ReadyToClose += (_, _) => closes++;

        var clean = _running.Begin(RunningAction.StorageClean);
        guard.CloseWhenIdle();
        guard.KeepOpen();
        clean.Dispose();

        Assert.Equal(0, closes);
        Assert.False(guard.ClosesWhenIdle);
    }

    /// <summary>
    /// Asked once, closed once. A later action beginning and ending is not a second request to close,
    /// which a window reopened by nothing would never expect.
    /// </summary>
    [Fact]
    public void AChoiceToCloseIsSpentByTheCloseItAskedFor()
    {
        var guard = new CloseGuard(_running);
        var closes = 0;
        guard.ReadyToClose += (_, _) => closes++;

        var clean = _running.Begin(RunningAction.StorageClean);
        guard.CloseWhenIdle();
        clean.Dispose();
        _running.Begin(RunningAction.ExploreRemoval).Dispose();

        Assert.Equal(1, closes);
    }

    /// <summary>Nothing closes a window the user never asked to close, however many runs end.</summary>
    [Fact]
    public void AnActionEndingClosesNothingWithoutAChoiceToClose()
    {
        var guard = new CloseGuard(_running);
        var closes = 0;
        guard.ReadyToClose += (_, _) => closes++;

        _running.Begin(RunningAction.StorageClean).Dispose();

        Assert.Equal(0, closes);
    }
}
