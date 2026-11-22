using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a clean set acting on the machine, which decides whether a tool's reading can stand for the next
/// one asked. Every case where it says yes wrongly is a reclaim measured against a store that changed.
/// </summary>
public sealed class RunActivityTests
{
    [Fact]
    public void AReadingBegunWhileNothingActsStandsUntilSomethingStarts()
    {
        var run = new RunActivity();
        var mark = Assert.NotNull(run.Quiet());

        Assert.True(run.StillQuiet(mark));

        using (run.Begin())
        {
            Assert.False(run.StillQuiet(mark));
        }

        Assert.False(run.StillQuiet(mark));
    }

    /// <summary>A step that started and ended between two readings changed what the first one read.</summary>
    [Fact]
    public void AStepThatStartedAndEndedSinceTheReadingBreaksIt()
    {
        var run = new RunActivity();
        var mark = Assert.NotNull(run.Quiet());

        run.Begin().Dispose();

        Assert.False(run.StillQuiet(mark));
    }

    /// <summary>A reading taken while a step writes may be of the machine before, after or part-way through the write.</summary>
    [Fact]
    public void NoReadingBegunWhileAStepActsCanStand()
    {
        var run = new RunActivity();

        using (run.Begin())
        {
            Assert.Null(run.Quiet());
        }

        Assert.NotNull(run.Quiet());
    }

    /// <summary>Two steps acting at once: the reading waits for both, not for the first to end.</summary>
    [Fact]
    public void AReadingCannotStandWhileAnyOfSeveralStepsStillActs()
    {
        var run = new RunActivity();
        var first = run.Begin();

        using (run.Begin())
        {
            first.Dispose();
            Assert.Null(run.Quiet());
        }

        Assert.NotNull(run.Quiet());
    }

    /// <summary>The time between two cleans is time Windows can change its component store in.</summary>
    [Fact]
    public void AReadingFromAnotherCleanNeverStands()
    {
        var earlier = new RunActivity();
        var mark = Assert.NotNull(earlier.Quiet());

        Assert.False(new RunActivity().StillQuiet(mark));
    }
}
