namespace Deguffer.Core.Memory;

/// <summary>
/// One read's processes, as the questions §7.2.1 asks of them: which can be identified at all, what
/// is under a process, and whether one process is under another.
///
/// <para>Built once from a snapshot and then asked, rather than derived afresh per question: one
/// close asks all three about the same read, and each derivation walks the whole process table
/// (G5).</para>
///
/// <para>Parents come from <see cref="ProcessForest"/> rather than from the recorded parent
/// identifiers, because Windows reuses identifiers and a "parent" created after its child is not its
/// parent (§7.2). The forest also puts nothing under a service host, so a broker or a packaged
/// application Windows started from a host is nobody else's descendant.</para>
/// </summary>
internal sealed class ProcessTree
{
    /// <summary>The idle process, which is not a process any of this is about.</summary>
    private const int IdleProcessId = 0;

    private readonly ProcessForest _forest;

    private ProcessTree(IReadOnlyList<ProcessMemory> measured, ProcessForest forest, bool identifies)
    {
        Measured = measured;
        _forest = forest;
        Identifies = identifies;
    }

    /// <summary>
    /// The processes this read can identify. A record whose creation time the figure check turned off
    /// has no identity to compare, and the idle process is not a process.
    /// </summary>
    public IReadOnlyList<ProcessMemory> Measured { get; }

    /// <summary>
    /// Whether this read can say what is running at all: its undocumented figures passed the check,
    /// so every record carries a creation time, and the walk reached the end of the table, so an
    /// absence is an exit rather than a record nobody read.
    ///
    /// <para>Both halves matter to a close. §5.6's assertions are comparisons of two reads by
    /// identifier and creation time, and §7.2.1's refusal of Deguffer's own process tree is a walk of
    /// the same records. A read that fails either test can answer neither, and saying so is what
    /// keeps a close from reporting a pass it never established.</para>
    /// </summary>
    public bool Identifies { get; }

    public static ProcessTree Of(MemorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        IReadOnlyList<ProcessMemory> measured =
        [
            .. snapshot.Processes.Processes.Where(p => p.ProcessId != IdleProcessId && p.CreationTime is not null),
        ];

        var hosted = snapshot.Services.Services.Select(service => service.ProcessId).ToHashSet();
        var hosts = measured.Select(process => process.ProcessId).Where(hosted.Contains).ToHashSet();

        return new ProcessTree(
            measured,
            ProcessForest.Of(measured, hosts),
            snapshot.Processes is { Figures: ProcessFigures.Checked, Complete: true });
    }

    /// <summary>Everything under <paramref name="root"/>, however deep, and empty where it has nothing.</summary>
    public IReadOnlyList<ProcessMemory> Under(ProcessMemory root)
    {
        ArgumentNullException.ThrowIfNull(root);

        // By identity rather than by reference: the caller's record came from this read in the
        // ordinary case, and an equal record from anywhere else must get the same answer.
        if (Measured.FirstOrDefault(p => Identity(p) == Identity(root)) is not { } start)
        {
            return [];
        }

        var under = new List<ProcessMemory>();
        var pending = new Stack<ProcessMemory>();
        pending.Push(start);

        // Without recursion, because nothing bounds how deep a machine's process tree is.
        while (pending.TryPop(out var process))
        {
            foreach (var child in _forest.ChildrenOf(process))
            {
                under.Add(child);
                pending.Push(child);
            }
        }

        return under;
    }

    /// <summary>The process holding <paramref name="processId"/>, or null where this read has none.</summary>
    public ProcessMemory? Holder(int processId) =>
        Measured.FirstOrDefault(process => process.ProcessId == processId);

    /// <summary>
    /// Whether <paramref name="ancestorId"/> holds a process that <paramref name="process"/> is under.
    ///
    /// <para>The ancestor is named by identifier alone because every caller is Deguffer asking about
    /// itself, and Deguffer's own identifier cannot have passed to anything else while Deguffer holds
    /// it.</para>
    /// </summary>
    public bool Descends(ProcessMemory process, int ancestorId)
    {
        ArgumentNullException.ThrowIfNull(process);

        return Holder(ancestorId) is { } ancestor
            && Under(ancestor).Any(p => Identity(p) == Identity(process));
    }

    /// <summary>Whether the process holding <paramref name="descendantId"/> is under <paramref name="process"/>.</summary>
    public bool Holds(ProcessMemory process, int descendantId)
    {
        ArgumentNullException.ThrowIfNull(process);

        return Under(process).Any(p => p.ProcessId == descendantId);
    }

    public static (int, long) Identity(ProcessMemory process)
    {
        ArgumentNullException.ThrowIfNull(process);

        return (process.ProcessId, process.CreationTime ?? 0);
    }
}
