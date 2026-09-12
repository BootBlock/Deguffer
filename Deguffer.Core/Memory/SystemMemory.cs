namespace Deguffer.Core.Memory;

/// <summary>What became of the memory lists in one read.</summary>
public enum MemoryListState
{
    /// <summary>Windows did not return them, or returned something of the wrong size.</summary>
    NotReturned = 0,

    /// <summary>
    /// They were returned, and their free, zeroed and standby pages did not add up to the available
    /// memory Windows documents, so they were not used.
    /// </summary>
    Disagrees,

    /// <summary>They were returned and agreed with the documented available memory.</summary>
    Checked,
}

/// <summary>
/// The page lists physical memory moves between, in bytes.
///
/// <para>Read through <c>SystemMemoryListInformation</c>, which Windows does not document. It needs no
/// elevation to read, which was measured rather than assumed, and that is the only reason it is used
/// at all: <c>GetPerformanceInfo</c> cannot tell a free page from a standby one.</para>
/// </summary>
/// <param name="Zeroed">Free pages already filled with zeroes.</param>
/// <param name="Free">Free pages not yet zeroed.</param>
/// <param name="Modified">
/// Pages taken from working sets that must be written somewhere before they can be reused, including
/// those with nowhere to be written.
/// </param>
/// <param name="Standby">
/// Cached pages no working set holds, across all eight priorities. Windows counts them as available
/// and hands them out the moment something needs them.
/// </param>
public sealed record MemoryLists(long Zeroed, long Free, long Modified, long Standby)
{
    /// <summary>
    /// How many pointer-sized counts <c>SYSTEM_MEMORY_LIST_INFORMATION</c> holds: the zeroed, free,
    /// modified, modified-no-write and bad counts, eight standby priorities, eight repurposed
    /// priorities, and the modified pages bound for the page file.
    /// </summary>
    internal const int CountLength = 22;

    private const int StandbyByPriority = 5;
    private const int Priorities = 8;

    /// <summary>The lists in bytes, from the page counts Windows returned.</summary>
    internal static MemoryLists FromPageCounts(ReadOnlySpan<nuint> counts, long pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(counts.Length, CountLength);

        long standby = 0;

        foreach (var count in counts.Slice(StandbyByPriority, Priorities))
        {
            standby += (long)count;
        }

        // Bad pages are not memory anything can use, and the repurposed counts are events rather
        // than pages on a list, so neither belongs in a picture of where memory is.
        return new MemoryLists(
            Zeroed: (long)counts[0] * pageSize,
            Free: (long)counts[1] * pageSize,
            Modified: ((long)counts[2] + (long)counts[3]) * pageSize,
            Standby: standby * pageSize);
    }
}

/// <summary>
/// The machine-wide figures, in bytes, from <c>GetPerformanceInfo</c>, which Windows documents.
/// </summary>
/// <param name="PhysicalTotal">Physical memory Windows manages, which excludes what the hardware reserves.</param>
/// <param name="Available">Standby, free and zeroed memory together: what can be handed out now.</param>
/// <param name="CommitCharge">Memory committed across the system, which is what allocations are charged against.</param>
/// <param name="CommitLimit">
/// How much can be committed before allocations fail: physical memory plus the page files, as they
/// stand now.
/// </param>
/// <param name="SystemCache">
/// What Windows documents as the standby list plus the system working set. Measured above the standby
/// list alone on one machine, so it is not treated as containing it exactly.
/// </param>
/// <param name="PagedPool">
/// Kernel paged pool, in memory or not. The part in memory is inside the system working set.
/// </param>
/// <param name="NonPagedPool">Kernel non-paged pool, which is always in memory.</param>
/// <param name="Lists">The memory lists, or null where <paramref name="ListState"/> is not <see cref="MemoryListState.Checked"/>.</param>
/// <param name="ListState">What became of the memory lists.</param>
public sealed record SystemMemory(
    long PhysicalTotal,
    long Available,
    long CommitCharge,
    long CommitLimit,
    long SystemCache,
    long PagedPool,
    long NonPagedPool,
    MemoryLists? Lists,
    MemoryListState ListState);
