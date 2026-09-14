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
    /// <summary>
    /// What the node the reader is pointing at is: what its part is, and for a service host which
    /// services it holds.
    ///
    /// <para>The names belong here rather than in the shape's label, which the picture trims to the
    /// width of the shape. A host of several services is the one case where the label cannot carry
    /// what <see cref="MemoryPart.Services"/> promises, so this is where that promise is kept.</para>
    /// </summary>
    public static string Describe(MemoryTree tree, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var part = Describe(tree.PartOf(node));
        var holds = ServiceHostText.Holds(tree.ServicesOf(node));

        return holds.Length == 0 ? part : $"{part} {holds}";
    }

    /// <summary>
    /// What <paramref name="part"/> is, and for a part of Windows why the size it is drawn at is
    /// normal. Never empty.
    /// </summary>
    public static string Describe(MemoryPart part) => part switch
    {
        MemoryPart.PhysicalMemory =>
            // No claim about the parts summing short: the remainder is drawn as "Not attributed"
            // precisely so that they do not, and where the figures overlap instead, the notes above
            // the picture say the parts add up to more. What is a lower bound is each measured
            // figure, not the picture.
            "The physical memory Windows manages. Every figure below it is a lower bound: unelevated, "
            + "some of what memory holds cannot be separated at all.",

        MemoryPart.Applications =>
            "Every process that hosts no service, under the process that started it where Windows "
            + "still records one. A program that runs as several processes is drawn as several, "
            + "because Windows accounts for memory one process at a time.",

        MemoryPart.Services =>
            "The processes that host services, each named by what it holds. Memory in a host shared "
            + "by several services cannot be divided between them, and pointing at that host names "
            + "them all.",

        MemoryPart.Windows =>
            "What Windows holds itself: its caches, its kernel memory, the lists it keeps of pages no "
            + "process is using, and the memory none of these figures attributes. It is often the "
            + "largest of the three parts, because it holds both the cache Windows keeps and "
            + "everything these figures could not separate.",

        MemoryPart.Process =>
            "A process, sized by the memory in RAM that no other process can use. What it holds "
            + "elsewhere, compressed or shared, is not in this figure, so a program can be using more "
            + "than its shape here shows.",

        MemoryPart.OwnShare =>
            "What this process holds itself, drawn beside the processes it started. Those are drawn "
            + "separately, so this is the parent's own pages rather than the total for the group.",

        MemoryPart.CompressionStore =>
            "Pages Windows compressed instead of writing them out. It grows when memory is being made "
            + "available, so a large one is the compression working, not a fault. The pages in it "
            + "still belong to the processes that own them, which is why their own figures do not "
            + "count them.",

        MemoryPart.SystemCache =>
            "The standby list and the system working set together, as Windows reports them. It is "
            + "drawn where the separate lists could not be read. Most of it is file data Windows kept "
            + "in case something reads it again, so a large figure here is cache rather than memory a "
            + "program is holding.",

        // "Pages Windows took out of working sets" rather than only file data: the list holds
        // private, page-file-backed pages as well, and a reader told otherwise would take the
        // largest shape on screen for something none of their programs had a stake in.
        MemoryPart.Standby =>
            "Cached pages no process holds: file data, program code, and pages Windows took out of "
            + "working sets, all kept in case something needs them again. A large figure here is "
            + "memory doing its job rather than memory gone astray, because Windows counts every "
            + "page on this list as available and hands it out the moment something needs it.",

        MemoryPart.Modified =>
            "Pages Windows has taken out of use and must write somewhere before it can reuse them. "
            + "Most move to the standby list as soon as that write is done, so the figure is normally "
            + "small and always changing, and the rest are the pages with nowhere to be written.",

        MemoryPart.Free =>
            "Pages nothing holds, ready to be handed out. A small figure here is normal rather than a "
            + "shortage: Windows keeps only as many as it needs ready, and the standby list beside it "
            + "counts as available too.",

        MemoryPart.NonPagedPool =>
            "Kernel memory that is never written out. It holds what the kernel and the drivers must "
            + "be able to reach at any moment, so what is in it follows the drivers on the machine "
            + "and how much the machine is doing. The paged pool is not drawn beside it: the part of "
            + "it in memory is inside the system working set, and its figure counts what has been "
            + "written out as well.",

        // Named rather than left to the arm below, so that a part added with no sentence of its own
        // fails the test that every part says what it is, instead of silently inheriting this one.
        // The compiler will not catch it: an enum switch needs a default arm whatever it covers.
        MemoryPart.Unattributed =>
            "Physical memory these figures cannot attribute: shared and shareable pages, page tables, "
            + "kernel stacks, memory drivers have locked, and the paged pool. Separating it needs "
            + "rights Deguffer does not ask for, so a large figure here is what could not be told "
            + "apart rather than memory that has gone missing.",

        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };
}
