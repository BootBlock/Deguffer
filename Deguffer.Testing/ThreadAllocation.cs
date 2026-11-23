namespace Deguffer.Testing;

/// <summary>
/// The bytes the calling thread allocates while a piece of code runs, for a test whose claim is what
/// that code allocates.
///
/// <para>The thread's count is more than the code allocated whenever the runtime's own work lands on
/// the thread during the read. A background collection running at the same time adds from a few
/// bytes to several kilobytes (#1047), and the thread pool's bookkeeping for a parallel loop the code
/// starts adds about 7 KB (#757). Either is added to a read and never taken from one, and neither
/// happens in every read, so the least of several reads is what the code allocates
/// (<see cref="Least"/>).</para>
/// </summary>
public static class ThreadAllocation
{
    /// <summary>The bytes the calling thread allocated during one run of <paramref name="read"/>.</summary>
    public static long During(Action read)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        read();

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// The least the calling thread allocated over <paramref name="reads"/> runs of
    /// <paramref name="read"/>, after one run that is not counted, because it pays for type loading,
    /// compilation and anything the code builds once.
    /// </summary>
    public static long Least(Action read, int reads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reads, 1);

        read();

        var least = long.MaxValue;

        for (var run = 0; run < reads; run++)
        {
            least = Math.Min(least, During(read));
        }

        return least;
    }
}
