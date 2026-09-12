namespace Deguffer.Core.Memory;

/// <summary>
/// What each part of a memory picture is, in words, for a reader pointing at one.
///
/// <para><b>It says what a part is and never what to do about it (§7.2).</b> Nothing here calls
/// anything unneeded, idle or safe to close, and nothing suggests closing, stopping or emptying
/// anything. Windows' own parts are the ones a reader is least likely to recognise, and the ones a
/// "RAM cleaner" would offer to empty, so they say what they hold and why it is normal.</para>
///
/// <para>In Core rather than in the shell because it is text about a rule, and a rule is testable
/// (G8).</para>
/// </summary>
public static class MemoryPartGuide
{
    /// <summary>One sentence or two about <paramref name="part"/>. Never empty.</summary>
    public static string Describe(MemoryPart part) => part switch
    {
        MemoryPart.PhysicalMemory =>
            "The physical memory Windows manages. Every figure below it is a lower bound: unelevated, "
            + "some of what memory holds cannot be separated at all.",

        MemoryPart.Applications =>
            "Every process that hosts no service, under the process that started it where Windows "
            + "still records one.",

        MemoryPart.Services =>
            "The processes that host services, each named by the services it holds. Memory in a host "
            + "shared by several services cannot be divided between them.",

        MemoryPart.Windows =>
            "What Windows holds itself, and the memory none of these figures attributes.",

        MemoryPart.Process =>
            "A process, sized by the memory in RAM that no other process can use. What it holds "
            + "elsewhere, compressed or shared, is not in this figure.",

        MemoryPart.OwnShare =>
            "What this process holds itself, drawn beside the processes it started.",

        MemoryPart.CompressionStore =>
            "Pages Windows compressed instead of writing them out. It grows when memory is being made "
            + "available, so a large one is the compression working, not a fault.",

        MemoryPart.SystemCache =>
            "The standby list and the system working set together, as Windows reports them. It is "
            + "drawn where the separate lists could not be read.",

        MemoryPart.Standby =>
            "Cached pages no process holds. Windows counts them as available and hands them out the "
            + "moment something needs them.",

        MemoryPart.Modified =>
            "Pages Windows has taken out of use and must write somewhere before it can reuse them.",

        MemoryPart.Free =>
            "Pages nothing holds, ready to be handed out.",

        MemoryPart.NonPagedPool =>
            "Kernel memory that is never written out. The paged pool is not drawn beside it: the part "
            + "of it in memory is inside the system working set, and its figure counts what has been "
            + "written out as well.",

        _ =>
            "Physical memory these figures cannot attribute: shared and shareable pages, page tables, "
            + "kernel stacks, memory drivers have locked, and the paged pool. Separating it needs "
            + "rights Deguffer does not ask for.",
    };
}
