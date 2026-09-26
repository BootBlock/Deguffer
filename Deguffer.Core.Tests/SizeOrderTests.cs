using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>§7 sorts the preview by size, and rows join it one provider at a time.</summary>
public sealed class SizeOrderTests
{
    [Fact]
    public void ALargerRowGoesAboveASmallerOne() =>
        Assert.Equal(1, SizeOrder.IndexFor([900, 100], 500));

    [Fact]
    public void TheLargestRowLeadsAndTheSmallestTrails()
    {
        Assert.Equal(0, SizeOrder.IndexFor([900, 100], 1_000));
        Assert.Equal(2, SizeOrder.IndexFor([900, 100], 50));
        Assert.Equal(0, SizeOrder.IndexFor([], 50));
    }

    /// <summary>A row goes below every row of its own size, so rows that tie keep the order they arrived in.</summary>
    [Fact]
    public void ARowOfEqualSizeGoesBelowTheOnesAlreadyThere() =>
        Assert.Equal(3, SizeOrder.IndexFor([900, 500, 500, 100], 500));

    /// <summary>
    /// A row shrunk by keeping an item stays where it is, so the list can be out of order. A newcomer
    /// goes before the first row smaller than itself rather than wherever a search assuming order
    /// would land.
    /// </summary>
    [Fact]
    public void ANewcomerGoesBeforeTheFirstSmallerRowInAListOutOfOrder() =>
        Assert.Equal(1, SizeOrder.IndexFor([900, 10, 800], 500));
}
