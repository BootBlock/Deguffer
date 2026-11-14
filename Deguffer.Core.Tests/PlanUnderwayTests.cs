using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How far through one plan a clean is, read back from how far through the whole run it is: the
/// inverse of how the plan's own progress is scaled into the run's.
/// </summary>
public sealed class PlanUnderwayTests
{
    private static readonly PlanUnderway Middle = new(new FakeCleanupProvider("middle"), PlanStage.Removing, From: 0.25, Share: 0.5);

    [Theory]
    [InlineData(0.25, 0)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.75, 1)]
    public void ThePlansOwnShareOfTheBarReadsBackAsHowFarThroughItTheRunIs(double overall, double within)
    {
        Assert.Equal(within, Middle.Within(overall), precision: 9);
    }

    /// <summary>Outside its span the plan is not started or is done, never part way or past the end.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void OutsideItsSpanThePlanIsEmptyOrFull(double overall, double within)
    {
        Assert.Equal(within, Middle.Within(overall));
    }

    /// <summary>A plan with no span to cross is done as soon as it is reached, rather than divided by zero.</summary>
    [Fact]
    public void APlanWithNoShareIsDoneAsSoonAsItIsReached()
    {
        var proof = Middle with { Stage = PlanStage.Checking, Share = 0 };

        Assert.Equal(1, proof.Within(0.25));
    }
}
