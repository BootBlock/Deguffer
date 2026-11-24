using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a Storage row states about its size and its items. The direction that matters is a figure
/// larger than what a clean takes, beside a checkbox that reads as full.
/// </summary>
public class ChosenOfWholeTests
{
    private static readonly ScanSize Chosen = ScanSize.FromLengths(300L * 1024 * 1024);

    private static readonly ScanSize Whole = ScanSize.FromLengths(18L * 1024 * 1024 * 1024);

    private static CleanupStep Step(long bytes) =>
        new DeleteDirectoryStep(@"C:\Users\testuser\AppData\Local\Fake\step", "step") { Estimated = ScanSize.FromLengths(bytes) };

    [Fact]
    public void ARowWithSomeOfItsStepsTickedTakesPart()
    {
        Assert.True(ChosenOfWhole.TakesPart([(Step(10), true, false), (Step(20), false, false)]));
    }

    [Fact]
    public void ARowWithAllOrNoneOfItsStepsTickedTakesTheWhole()
    {
        Assert.False(ChosenOfWhole.TakesPart([(Step(10), true, false), (Step(20), true, false)]));
        Assert.False(ChosenOfWhole.TakesPart([(Step(10), false, false), (Step(20), false, false)]));
        Assert.False(ChosenOfWhole.TakesPart([]));
    }

    /// <summary>
    /// A kept item is not in the row's plan, and a step that removes nothing adds nothing to its whole,
    /// so leaving either unticked takes nothing away from what the row's figure states.
    /// </summary>
    [Fact]
    public void AStepTheWholeDoesNotCountCannotMakeThePart()
    {
        Assert.False(ChosenOfWhole.TakesPart([(Step(10), true, false), (Step(20), false, true)]));
        Assert.False(ChosenOfWhole.TakesPart([(Step(10), true, false), (Step(0), false, false)]));
    }

    [Fact]
    public void APartStatesTheChosenSizeAgainstTheWhole()
    {
        Assert.Equal($"{FreeSpace.Format(Chosen)} of {FreeSpace.Format(Whole)}", ChosenOfWhole.Size(true, Chosen, Whole));
    }

    [Fact]
    public void TheWholeIsStatedAlone()
    {
        Assert.Equal(FreeSpace.Format(Whole), ChosenOfWhole.Size(false, Whole, Whole));
    }

    /// <summary>A hedge on either figure stays on it, so a forecast is never stated as a measurement.</summary>
    [Fact]
    public void AnApproximateFigureKeepsItsHedge()
    {
        var whole = ScanSize.Approximate(2048);

        Assert.EndsWith($"of {FreeSpace.Format(whole)}", ChosenOfWhole.Size(true, ScanSize.FromLengths(1024), whole));
        Assert.StartsWith("~", FreeSpace.Format(whole));
    }

    [Fact]
    public void SomeOfTheItemsTickedStatesHowMany()
    {
        Assert.Equal("2 of 40 items", ChosenOfWhole.Items(2, 40));
        Assert.Equal("1 of 2 items", ChosenOfWhole.Items(1, 2));
    }

    [Fact]
    public void AllOrNoneOfTheItemsTickedStatesTheCountAsEnglishForOneAndForMany()
    {
        Assert.Equal("1 item", ChosenOfWhole.Items(1, 1));
        Assert.Equal("40 items", ChosenOfWhole.Items(0, 40));
        Assert.Equal("40 items", ChosenOfWhole.Items(40, 40));
    }
}
