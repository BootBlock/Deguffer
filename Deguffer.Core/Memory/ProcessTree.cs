namespace Deguffer.Core.Memory;

/// <summary>
/// One snapshot's processes as a forest, for the two questions §7.2.1 asks of one: what is under a
/// process, and whether a process is under another.
///
/// <para>Both go through <see cref="ProcessForest"/> rather than through the recorded parent
/// identifiers, because Windows reuses identifiers and a "parent" created after its child is not its
/// parent (§7.2). The forest also puts nothing under a service host, so a broker or a packaged
/// application Windows started from a host is nobody else's descendant.</para>
/// </summary>
internal static class ProcessTree
{
    /// <summary>The idle process, which is not a process any of this is about.</summary>
    private const int IdleProcessId = 0;

    /// <summary>
    /// The processes of <paramref name="snapshot"/> that can be identified at all. A record whose
    /// creation time the figure check turned off has no identity to compare, and the idle process is
    /// not a process.
    /// </summary>
    public static IReadOnlyList<ProcessMemory> Measured(MemorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return
        [
            .. snapshot.Processes.Processes.Where(p => p.ProcessId != IdleProcessId && p.CreationTime is not null),
        ];
    }

    /// <summary>Everything under <paramref name="root"/>, however deep, and empty where it has nothing.</summary>
    public static IReadOnlyList<ProcessMemory> Under(MemorySnapshot snapshot, ProcessMemory root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var measured = Measured(snapshot);

        // By identity rather than by reference: the caller's record came from this snapshot in the
        // ordinary case, and an equal record from anywhere else must get the same answer.
        return measured.FirstOrDefault(p => Identity(p) == Identity(root)) is { } start
            ? Under(ForestOf(snapshot, measured), start)
            : [];
    }

    /// <summary>
    /// Whether <paramref name="process"/> is under the process holding <paramref name="ancestorId"/>.
    ///
    /// <para>The ancestor is named by identifier alone because the caller is Deguffer asking about
    /// itself, and Deguffer's own identifier cannot have passed to anything else while Deguffer holds
    /// it.</para>
    /// </summary>
    public static bool Descends(MemorySnapshot snapshot, ProcessMemory process, int ancestorId)
    {
        ArgumentNullException.ThrowIfNull(process);

        var measured = Measured(snapshot);

        return measured.FirstOrDefault(p => p.ProcessId == ancestorId) is { } ancestor
            && Under(ForestOf(snapshot, measured), ancestor).Any(p => Identity(p) == Identity(process));
    }

    public static (int, long) Identity(ProcessMemory process)
    {
        ArgumentNullException.ThrowIfNull(process);

        return (process.ProcessId, process.CreationTime ?? 0);
    }

    private static ProcessForest ForestOf(MemorySnapshot snapshot, IReadOnlyList<ProcessMemory> measured)
    {
        var hosted = snapshot.Services.Services.Select(service => service.ProcessId).ToHashSet();

        return ProcessForest.Of(measured, measured.Select(p => p.ProcessId).Where(hosted.Contains).ToHashSet());
    }

    /// <summary>Without recursion, because nothing bounds how deep a machine's process tree is.</summary>
    private static IReadOnlyList<ProcessMemory> Under(ProcessForest forest, ProcessMemory root)
    {
        var under = new List<ProcessMemory>();
        var pending = new Stack<ProcessMemory>();
        pending.Push(root);

        while (pending.TryPop(out var process))
        {
            foreach (var child in forest.ChildrenOf(process))
            {
                under.Add(child);
                pending.Push(child);
            }
        }

        return under;
    }
}
