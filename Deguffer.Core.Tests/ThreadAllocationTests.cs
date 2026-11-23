using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The thread's allocation as the cost tests read it. A reading that counted nothing would pass every
/// one of them, since an equality or a bound holds of nothing, so these show that it counts what the
/// code allocates and leaves out only what lands on a single read.
/// </summary>
public sealed class ThreadAllocationTests
{
    private const int Length = 1_000;

    private const int Reads = 5;

    [Fact]
    public void AReadCountsWhatTheCodeAllocated()
    {
        byte[]? kept = null;

        var allocated = ThreadAllocation.During(() => kept = new byte[Length]);

        Assert.InRange(allocated, Length, Length + 64);
        GC.KeepAlive(kept);
    }

    [Fact]
    public void TheLeastOfSeveralReadsLeavesOutWhatLandedOnSome()
    {
        byte[]? kept = null;
        var run = 0;

        var plain = ThreadAllocation.Least(() => kept = new byte[Length], Reads);
        // On the first counted read and the last, after the uncounted run, so that taking either
        // read alone in place of the least counts the extra.
        var withExtras = ThreadAllocation.Least(
            () => kept = ++run is 2 or Reads + 1 ? new byte[Length * 10] : new byte[Length],
            Reads);

        Assert.Equal(plain, withExtras);
        Assert.InRange(plain, Length, Length + 64);
        GC.KeepAlive(kept);
    }

    [Fact]
    public void TheFirstRunIsNotCounted()
    {
        byte[]? kept = null;
        var run = 0;

        var plain = ThreadAllocation.Least(() => kept = new byte[Length], reads: 1);
        var buildingOnce = ThreadAllocation.Least(
            () => kept = ++run == 1 ? new byte[Length * 10] : new byte[Length],
            reads: 1);

        Assert.Equal(plain, buildingOnce);
        GC.KeepAlive(kept);
    }

    [Fact]
    public void NoReadsIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ThreadAllocation.Least(() => { }, reads: 0));
}
