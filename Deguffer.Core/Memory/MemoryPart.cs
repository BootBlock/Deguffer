namespace Deguffer.Core.Memory;

/// <summary>What one node of a <see cref="MemoryTree"/> stands for.</summary>
public enum MemoryPart
{
    /// <summary>The root: the physical memory Windows manages.</summary>
    PhysicalMemory,

    /// <summary>Every process that hosts no service, by the tree of processes that started it.</summary>
    Applications,

    /// <summary>
    /// Every process that hosts a service, each at the top of this part. Nothing a host started sits
    /// under it: Windows starts packaged applications and brokers from a service host, and those are
    /// applications rather than memory a service holds.
    /// </summary>
    Services,

    /// <summary>What Windows itself holds, and the memory no figure attributes.</summary>
    Windows,

    /// <summary>One process. It holds its children and its own share where it has children.</summary>
    Process,

    /// <summary>
    /// A process's own private working set, drawn beside its children, as a folder's loose files are
    /// drawn beside its subfolders.
    /// </summary>
    OwnShare,

    /// <summary>The working set of the process that holds compressed pages.</summary>
    CompressionStore,

    /// <summary>
    /// <c>GetPerformanceInfo</c>'s system cache: drawn only where the memory lists could not be used,
    /// because it overlaps the standby list they measure.
    /// </summary>
    SystemCache,

    /// <summary>Cached pages no working set holds, which Windows hands out the moment something needs them.</summary>
    Standby,

    /// <summary>Pages waiting to be written before they can be reused.</summary>
    Modified,

    /// <summary>Free and zeroed pages.</summary>
    Free,

    /// <summary>Kernel memory that is never paged out.</summary>
    NonPagedPool,

    /// <summary>
    /// Physical memory none of the other parts accounts for, which unelevated figures cannot divide
    /// without counting a page twice (§7.2).
    ///
    /// <para>What that leaves depends on which parts were drawn. Always: shared and shareable pages,
    /// page tables, kernel stacks, driver-locked memory and the paged pool. Where the memory lists were
    /// drawn: the system working set. Where the system cache was drawn instead: the free, zeroed and
    /// modified pages. Where the per-process figures were off: every process's own memory as well.</para>
    /// </summary>
    Unattributed,
}

/// <summary>
/// What a node is, independently of where one tree numbered it: a part of Windows by its part, and a
/// process by its identifier and creation time, which together name exactly one process even though
/// Windows reuses identifiers.
/// </summary>
/// <param name="ProcessId">Zero for anything that is not a process.</param>
/// <param name="CreationTime">The process's creation time as a FILETIME, or zero for anything that is not a process.</param>
public readonly record struct MemoryNodeKey(MemoryPart Part, int ProcessId, long CreationTime)
{
    public static MemoryNodeKey Of(MemoryPart part) => new(part, 0, 0);
}
