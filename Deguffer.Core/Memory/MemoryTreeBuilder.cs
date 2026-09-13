namespace Deguffer.Core.Memory;

/// <summary>
/// Builds the <see cref="MemoryTree"/> of one snapshot: its processes as Applications and Services,
/// and what Windows holds beside them, with the memory no figure attributes drawn as a part of its own.
///
/// <para><b>No page is drawn twice (§7.2).</b> Every part is one figure that no other drawn part
/// contains: the private working sets, the compression store's working set, the non-paged pool, and
/// either the three memory lists or, where those could not be used, the system cache. The system cache
/// and the standby list are never drawn together, because the one overlaps the other. The paged pool is
/// not drawn at all: the part of it in memory is inside the system working set, and its figure counts
/// what is paged out as well, so it stays in the remainder.</para>
///
/// <para>Built once per snapshot, and never updated: a refresh builds a new tree.</para>
/// </summary>
public static class MemoryTreeBuilder
{
    /// <summary>
    /// The image name Windows gives the process holding compressed pages. A name rather than a flag,
    /// because nothing unelevated marks that process otherwise, so it is taken only together with the
    /// parent every such process has: <see cref="SystemProcessId"/>.
    /// </summary>
    public const string CompressionStoreName = "Memory Compression";

    /// <summary>The identifier Windows gives the System process.</summary>
    public const int SystemProcessId = 4;

    /// <summary>The idle process, which holds no memory of its own worth drawing and has no name.</summary>
    private const int IdleProcessId = 0;

    public static MemoryTree Build(MemorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var drafts = new TreeDrafts();
        var root = drafts.Add("Physical memory", MemoryPart.PhysicalMemory, parent: 0, bytes: 0);
        var applications = drafts.Add("Applications", MemoryPart.Applications, root, bytes: 0);
        var services = drafts.Add("Services", MemoryPart.Services, root, bytes: 0);
        var windows = drafts.Add("Windows", MemoryPart.Windows, root, bytes: 0);

        var compressionStore = AddProcesses(drafts, snapshot, applications, services);

        AddWindows(drafts, snapshot.System, compressionStore, windows);

        return drafts.Finish(snapshot);
    }

    /// <summary>
    /// Place every measured process under Applications or Services, and hand back the compression store's
    /// process where there is one, which belongs to Windows instead.
    ///
    /// <para>Nothing is placed where the figures are off: without a checked private working set there
    /// is nothing to size a process by, and without a checked creation time no parent link or identity
    /// can be trusted (§7.2).</para>
    /// </summary>
    private static ProcessMemory? AddProcesses(TreeDrafts drafts, MemorySnapshot snapshot, int applications, int services)
    {
        if (snapshot.Processes.Figures != ProcessFigures.Checked)
        {
            return null;
        }

        var hosted = snapshot.Services.Services.ToLookup(service => service.ProcessId);
        var measured = new List<ProcessMemory>(snapshot.Processes.Processes.Count);
        ProcessMemory? compressionStore = null;

        foreach (var process in snapshot.Processes.Processes)
        {
            if (process.ProcessId == IdleProcessId || process.PrivateWorkingSet is null || process.CreationTime is null)
            {
                continue;
            }

            if (compressionStore is null
                && process.ParentProcessId == SystemProcessId
                && string.Equals(process.Name, CompressionStoreName, StringComparison.Ordinal))
            {
                compressionStore = process;
                continue;
            }

            measured.Add(process);
        }

        var hosts = measured.Select(process => process.ProcessId).Where(hosted.Contains).ToHashSet();
        var forest = ProcessForest.Of(measured, hosts);

        // One stack for the whole forest rather than one per root. Each walk drains it, and a memory
        // view rebuilds this tree every couple of seconds for as long as it is on screen (G5).
        var pending = new Stack<(ProcessMemory Process, int Parent)>();

        foreach (var root in forest.Roots)
        {
            Place(drafts, forest, hosted, root, hosts.Contains(root.ProcessId) ? services : applications, pending);
        }

        return compressionStore;
    }

    /// <summary>
    /// One process and everything the forest put under it. A process with children becomes a node
    /// holding them and a node for its own share, so its own memory is drawn beside theirs rather than
    /// hidden in the frame round them.
    /// </summary>
    /// <param name="pending">Drained by the time this returns, so one serves every root.</param>
    private static void Place(
        TreeDrafts drafts,
        ProcessForest forest,
        ILookup<int, RunningService> hosted,
        ProcessMemory root,
        int part,
        Stack<(ProcessMemory Process, int Parent)> pending)
    {
        pending.Push((root, part));

        while (pending.TryPop(out var frame))
        {
            var (process, parent) = frame;
            var ownBytes = process.PrivateWorkingSet!.Value;
            var children = forest.ChildrenOf(process);

            if (children.Count == 0)
            {
                // Only a node with nothing under it can hold services: nothing is placed under a
                // service host, so a process with children hosts none.
                drafts.Add(
                    process.Name, MemoryPart.Process, parent, ownBytes, process, hosted[process.ProcessId].ToArray());
                continue;
            }

            var node = drafts.Add(process.Name, MemoryPart.Process, parent, bytes: 0, process);
            drafts.Add(process.Name, MemoryPart.OwnShare, node, ownBytes, process);

            foreach (var child in children)
            {
                pending.Push((child, node));
            }
        }
    }

    private static void AddWindows(TreeDrafts drafts, SystemMemory system, ProcessMemory? compressionStore, int windows)
    {
        if (compressionStore is { PrivateWorkingSet: { } compressed })
        {
            drafts.Add("Compression store", MemoryPart.CompressionStore, windows, compressed, compressionStore);
        }

        if (system.Lists is { } lists)
        {
            drafts.Add("Standby", MemoryPart.Standby, windows, lists.Standby);
            drafts.Add("Modified", MemoryPart.Modified, windows, lists.Modified);
            drafts.Add("Free", MemoryPart.Free, windows, lists.Zeroed + lists.Free);
        }
        else
        {
            drafts.Add("System cache", MemoryPart.SystemCache, windows, system.SystemCache);
        }

        drafts.Add("Non-paged pool", MemoryPart.NonPagedPool, windows, system.NonPagedPool);

        // Last, because it is whatever every other part leaves.
        var remainder = system.PhysicalTotal - drafts.Attributed;
        drafts.Add("Not attributed", MemoryPart.Unattributed, windows, Math.Max(0, remainder));
        drafts.Overcount = Math.Max(0, -remainder);
    }
}
