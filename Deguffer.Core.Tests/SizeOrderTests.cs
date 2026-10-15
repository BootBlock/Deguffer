using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>§7 sorts the preview by size, and rows join it as their providers finish.</summary>
public sealed class SizeOrderTests
{
    [Fact]
    public void ALargerRowGoesAboveASmallerOne() =>
        Assert.Equal(1, SizeOrder.IndexFor([(900, 5), (100, 6)], 500, place: 0));

    [Fact]
    public void TheLargestRowLeadsAndTheSmallestTrails()
    {
        Assert.Equal(0, SizeOrder.IndexFor([(900, 0), (100, 1)], 1_000, place: 9));
        Assert.Equal(2, SizeOrder.IndexFor([(900, 8), (100, 9)], 50, place: 0));
        Assert.Equal(0, SizeOrder.IndexFor([], 50, place: 0));
    }

    /// <summary>
    /// Rows of one size stand in their providers' order, whichever finished first. Providers are
    /// planned several at once, so a tie broken by arrival would list the rows that found nothing in a
    /// different order after every scan.
    /// </summary>
    [Fact]
    public void ARowOfEqualSizeGoesByItsProvidersPlaceNotByWhenItArrived()
    {
        Assert.Equal(3, SizeOrder.IndexFor([(900, 0), (500, 1), (500, 2), (100, 3)], 500, place: 7));
        Assert.Equal(1, SizeOrder.IndexFor([(900, 0), (500, 4), (500, 6), (100, 3)], 500, place: 2));
        Assert.Equal(2, SizeOrder.IndexFor([(900, 0), (500, 4), (500, 6), (100, 3)], 500, place: 5));
    }

    /// <summary>
    /// A row shrunk by keeping an item stays where it is, so the list can be out of order. A newcomer
    /// goes before the first row smaller than itself rather than wherever a search assuming order
    /// would land.
    /// </summary>
    [Fact]
    public void ANewcomerGoesBeforeTheFirstSmallerRowInAListOutOfOrder() =>
        Assert.Equal(1, SizeOrder.IndexFor([(900, 0), (10, 1), (800, 2)], 500, place: 3));
}
