using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// Space two rows each offer to free is counted once in a total across rows, at the most either
/// offers, and space nobody shares is summed as it always was.
/// </summary>
public sealed class ReclaimPoolTests
{
    private static readonly ReclaimPool Store = new("store");

    private static RunCommandStep Step(long bytes, ReclaimPool? pool = null) =>
        new("tool.exe", $"--free {bytes}", "Free space") { Estimated = ScanSize.FromLengths(bytes), SharesReclaim = pool };

    [Fact]
    public void StepsOutsideAPoolAreSummed() =>
        Assert.Equal(30, ReclaimPool.Total([Step(10), Step(20)]).Reclaimable);

    [Fact]
    public void APoolIsCountedOnceAtItsLargestStep() =>
        Assert.Equal(12 + 5, ReclaimPool.Total([Step(12, Store), Step(9, Store), Step(5)]).Reclaimable);

    [Fact]
    public void PoolsWithDifferentNamesAreSeparate() =>
        Assert.Equal(12 + 9, ReclaimPool.Total([Step(12, Store), Step(9, new ReclaimPool("other"))]).Reclaimable);

    [Fact]
    public void ApproximationSurvivesThePool() =>
        Assert.True(ReclaimPool.Total(
        [
            Step(1) with { Estimated = ScanSize.Approximate(12), SharesReclaim = Store },
            Step(9, Store),
        ]).IsApproximate);
}
