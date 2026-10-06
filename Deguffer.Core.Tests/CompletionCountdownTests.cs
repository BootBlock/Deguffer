using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The warning before what follows a clean. It must run its whole length, start again while anything
/// else in Deguffer is changing the machine, and say what is about to happen.
/// </summary>
public sealed class CompletionCountdownTests
{
    private static readonly RunningActions Idle = new();

    [Fact]
    public void IsDueOnlyOnceTheWholeCountHasRun()
    {
        var countdown = new CompletionCountdown(CompletionAction.ShutDown);

        for (var second = 1; second < CompletionCountdown.Seconds; second++)
        {
            Assert.False(countdown.Tick(Idle));
        }

        Assert.Equal(1, countdown.SecondsLeft);
        Assert.True(countdown.Tick(Idle));
        Assert.Equal(0, countdown.SecondsLeft);
    }

    /// <summary>Ending the process under a removal on another page loses its §5.6 check and its report.</summary>
    [Fact]
    public void StartsAgainWhileAnythingElseRuns()
    {
        var countdown = new CompletionCountdown(CompletionAction.Restart);

        for (var second = 1; second < CompletionCountdown.Seconds; second++)
        {
            countdown.Tick(Idle);
        }

        var running = new RunningActions();
        using (running.Begin(RunningAction.ExploreRemoval))
        {
            Assert.False(countdown.Tick(running));
            Assert.Equal(CompletionCountdown.Seconds, countdown.SecondsLeft);
        }

        Assert.False(countdown.Tick(running));
        Assert.Equal(CompletionCountdown.Seconds - 1, countdown.SecondsLeft);
    }

    [Fact]
    public void SaysWhatWillHappenAndWhen()
    {
        var countdown = new CompletionCountdown(CompletionAction.Lock);

        Assert.Equal("Deguffer will lock this PC in 30 seconds.", countdown.Sentence(Idle));

        for (var second = 1; second < CompletionCountdown.Seconds; second++)
        {
            countdown.Tick(Idle);
        }

        Assert.Equal("Deguffer will lock this PC in 1 second.", countdown.Sentence(Idle));
        Assert.Equal("Lock now", countdown.ActNowLabel);
        Assert.Equal("Cancel", countdown.CancelLabel);
    }

    [Fact]
    public void SaysWhatItIsWaitingFor()
    {
        var countdown = new CompletionCountdown(CompletionAction.ShutDown);
        var running = new RunningActions();
        using var removal = running.Begin(RunningAction.ExploreRemoval);

        Assert.Equal(
            "Deguffer will shut down this PC 30 seconds after a removal on the Explore page finishes.",
            countdown.Sentence(running));

        using var uninstall = running.Begin(RunningAction.Uninstall);

        Assert.Equal(
            "Deguffer will shut down this PC 30 seconds after a removal on the Explore page and an uninstall finish.",
            countdown.Sentence(running));
    }

    /// <summary>Only the choices that close every program on the machine ask for work to be saved.</summary>
    [Theory]
    [InlineData(CompletionAction.LogOff, true)]
    [InlineData(CompletionAction.Restart, true)]
    [InlineData(CompletionAction.ShutDown, true)]
    [InlineData(CompletionAction.ExitDeguffer, false)]
    [InlineData(CompletionAction.Lock, false)]
    [InlineData(CompletionAction.Sleep, false)]
    [InlineData(CompletionAction.Hibernate, false)]
    public void AsksForWorkToBeSavedOnlyWhereProgramsWillClose(CompletionAction action, bool asks)
    {
        var sentence = new CompletionCountdown(action).Sentence(Idle);

        Assert.Equal(asks, sentence.EndsWith(" Save your work in other programs first.", StringComparison.Ordinal));
    }

    [Fact]
    public void ThereIsNoCountdownToNothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompletionCountdown(CompletionAction.Nothing));
    }
}
