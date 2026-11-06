namespace Deguffer.Benchmark;

/// <summary>
/// The collector's process-wide counters at one moment, or the difference between two moments.
///
/// <para>Process-wide because a collection is: it suspends every managed thread, so one started by
/// an index build's allocations stops a walk running beside it as well, and the time it costs the
/// walk is the time the process was paused while the walk ran.</para>
/// </summary>
/// <param name="Paused">How long the collector held the process's threads suspended.</param>
/// <param name="Collections">How many collections of any generation ran.</param>
/// <param name="Gen1">How many of them were of generation 1 or 2.</param>
/// <param name="Gen2">How many of them were of generation 2, the full collections.</param>
/// <param name="Allocated">The bytes allocated on every thread.</param>
internal readonly record struct HeapCounters(TimeSpan Paused, int Collections, int Gen1, int Gen2, long Allocated)
{
    /// <summary>How many collections were of generation 0 alone.</summary>
    public int Gen0Only => Collections - Gen1;

    /// <summary>How many collections were of generation 1 alone.</summary>
    public int Gen1Only => Gen1 - Gen2;

    /// <summary>
    /// The counters now. The allocation count is the precise one, because the build allocates on
    /// several threads and the imprecise figure leaves out what each has not yet handed back.
    /// </summary>
    public static HeapCounters Now() => new(
        GC.GetTotalPauseDuration(),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        GC.GetTotalAllocatedBytes(precise: true));

    public static HeapCounters operator -(HeapCounters after, HeapCounters before) => new(
        after.Paused - before.Paused,
        after.Collections - before.Collections,
        after.Gen1 - before.Gen1,
        after.Gen2 - before.Gen2,
        after.Allocated - before.Allocated);
}
