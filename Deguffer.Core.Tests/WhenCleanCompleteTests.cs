using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which choices the Storage page offers for after a clean, and which finished cleans are followed by
/// the one chosen. Every rule here decides whether Deguffer ends a session or turns a machine off.
/// </summary>
public sealed class WhenCleanCompleteTests
{
    [Fact]
    public void OffersEveryChoiceInOrderWhereTheMachineCanDoThemAll()
    {
        Assert.Equal(
            [
                CompletionAction.Nothing,
                CompletionAction.ExitDeguffer,
                CompletionAction.Lock,
                CompletionAction.LogOff,
                CompletionAction.Sleep,
                CompletionAction.Hibernate,
                CompletionAction.Restart,
                CompletionAction.ShutDown,
            ],
            WhenCleanComplete.Offered(canSleep: true, canHibernate: true));
    }

    [Theory]
    [InlineData(false, true, CompletionAction.Sleep)]
    [InlineData(true, false, CompletionAction.Hibernate)]
    public void LeavesOutWhatTheMachineCannotDo(bool canSleep, bool canHibernate, CompletionAction missing)
    {
        var offered = WhenCleanComplete.Offered(canSleep, canHibernate);

        Assert.DoesNotContain(missing, offered);
        Assert.Equal(7, offered.Count);
    }

    [Fact]
    public void FindsAStoredChoiceInTheList()
    {
        var offered = WhenCleanComplete.Offered(canSleep: false, canHibernate: true);

        Assert.Equal(offered.Count - 1, WhenCleanComplete.IndexOf(CompletionAction.ShutDown, offered));
        Assert.Equal(4, WhenCleanComplete.IndexOf(CompletionAction.Hibernate, offered));
    }

    /// <summary>Any reading but Do nothing would end a session nobody asked to end.</summary>
    [Theory]
    [InlineData(CompletionAction.Sleep)]
    [InlineData((CompletionAction)99)]
    public void ReadsAChoiceThatIsNotOfferedAsDoNothing(CompletionAction stored)
    {
        var offered = WhenCleanComplete.Offered(canSleep: false, canHibernate: false);

        Assert.Equal(CompletionAction.Nothing, offered[WhenCleanComplete.IndexOf(stored, offered)]);
    }

    [Theory]
    [InlineData(RunVerdict.AllSurvived)]
    [InlineData(RunVerdict.Unverified)]
    [InlineData(RunVerdict.RemovedFromOutside)]
    public void FollowsACleanThatRanToTheEnd(RunVerdict verdict)
    {
        Assert.True(WhenCleanComplete.Follows(CompletionAction.ShutDown, new RunOutcome("Cleaned.", verdict, Cancelled: false)));
    }

    /// <summary>The one sentence on the screen that must be read before the next run.</summary>
    [Fact]
    public void DoesNotFollowAVerificationFailure()
    {
        Assert.False(WhenCleanComplete.Follows(
            CompletionAction.ShutDown,
            new RunOutcome("Cleaned, but verification failed.", RunVerdict.VerificationFailed, Cancelled: false)));
    }

    /// <summary>Whoever cancelled it is at the machine.</summary>
    [Fact]
    public void DoesNotFollowACancelledClean()
    {
        Assert.False(WhenCleanComplete.Follows(
            CompletionAction.ShutDown,
            new RunOutcome("Clean cancelled part-way.", RunVerdict.AllSurvived, Cancelled: true)));
    }

    [Fact]
    public void DoNothingFollowsNothing()
    {
        Assert.False(WhenCleanComplete.Follows(
            CompletionAction.Nothing,
            new RunOutcome("All protected paths survived.", RunVerdict.AllSurvived, Cancelled: false)));
    }

    [Fact]
    public void NamesEveryChoice()
    {
        Assert.All(Enum.GetValues<CompletionAction>(), action => Assert.NotEmpty(WhenCleanComplete.Label(action)));
        Assert.Equal("Shut down PC", WhenCleanComplete.Label(CompletionAction.ShutDown));
    }

    [Fact]
    public void ARefusalSaysWhatWasNotDoneAndWhy()
    {
        Assert.Equal(
            "The clean finished, but Deguffer could not log you off. Access is denied.",
            WhenCleanComplete.Refused(CompletionAction.LogOff, "Access is denied."));
    }

    [Fact]
    public void TheExplanationStatesTheCountdownItIsCarriedOutBehind()
    {
        Assert.Contains($"counts down for {CompletionCountdown.Seconds} seconds", WhenCleanComplete.Explanation);
    }
}
