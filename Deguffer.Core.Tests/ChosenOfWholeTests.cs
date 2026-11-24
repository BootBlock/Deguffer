using Deguffer.Core.Choosing;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a Storage row states about its size and its items. The defect this stops is a mixed row that
/// states its whole size beside a checkbox that reads as full, when the clean would take a fraction of it.
/// </summary>
public class ChosenOfWholeTests
{
    private static readonly ScanSize Chosen = ScanSize.FromLengths(300L * 1024 * 1024);

    private static readonly ScanSize Whole = ScanSize.FromLengths(18L * 1024 * 1024 * 1024);

    [Fact]
    public void AMixedRowStatesTheChosenSizeAgainstTheWhole()
    {
        Assert.Equal($"{FreeSpace.Format(Chosen)} of {FreeSpace.Format(Whole)}", ChosenOfWhole.Size(null, Chosen, Whole));
    }

    /// <summary>A full row takes the whole, and a clear one offers it, so neither states a part.</summary>
    [Fact]
    public void ATickedOrClearRowStatesTheWhole()
    {
        Assert.Equal(FreeSpace.Format(Whole), ChosenOfWhole.Size(true, Whole, Whole));
        Assert.Equal(FreeSpace.Format(Whole), ChosenOfWhole.Size(false, ScanSize.Zero, Whole));
    }

    /// <summary>A hedge on either figure stays on it, so a forecast is never stated as a measurement.</summary>
    [Fact]
    public void AnApproximateFigureKeepsItsHedge()
    {
        var whole = ScanSize.Approximate(2048);

        Assert.EndsWith($"of {FreeSpace.Format(whole)}", ChosenOfWhole.Size(null, ScanSize.FromLengths(1024), whole));
        Assert.StartsWith("about ", FreeSpace.Format(whole));
    }

    [Fact]
    public void AMixedRowStatesHowManyOfItsItemsAreChosen()
    {
        Assert.Equal("2 of 40 items", ChosenOfWhole.Items(null, 2, 40));
        Assert.Equal("1 of 2 items", ChosenOfWhole.Items(null, 1, 2));
    }

    [Fact]
    public void ATickedOrClearRowStatesItsItemCountAsEnglishForOneAndForMany()
    {
        Assert.Equal("1 item", ChosenOfWhole.Items(true, 1, 1));
        Assert.Equal("40 items", ChosenOfWhole.Items(false, 0, 40));
        Assert.Equal("40 items", ChosenOfWhole.Items(true, 40, 40));
    }
}
