namespace Deguffer.Benchmark.Tests;

public sealed class SpreadTests
{
    [Fact]
    public void AnOddNumberOfRunsTakesTheMiddleOne() =>
        Assert.Equal(new Spread(3, 1, 9), Spread.Of([9, 1, 3]));

    [Fact]
    public void AnEvenNumberOfRunsTakesTheMeanOfTheMiddleTwo() =>
        Assert.Equal(new Spread(2.5, 1, 40), Spread.Of([40, 2, 1, 3]));

    [Fact]
    public void OneRunIsItsOwnMedianAndRange() =>
        Assert.Equal(new Spread(7, 7, 7), Spread.Of([7]));

    [Fact]
    public void NoRunsHaveNoMedian() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Spread.Of([]));
}
