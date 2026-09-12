namespace Deguffer.Core.Memory;

/// <summary>
/// The processes of one snapshot as a forest: each under the parent its creation time allows, and
/// every other one at a root of its own.
///
/// <para><b>A recorded parent identifier is a hint.</b> The parent may have exited and its identifier
/// gone to a later process, so a parent counts only where it was created no later than its child. A
/// process whose parent has gone, or whose parent's identifier now belongs to a later process, is a
/// root (§7.2).</para>
///
/// <para><b>No process is placed twice, and none is lost.</b> Links that the creation times allow can
/// still form a cycle where two processes share a creation time, and a cycle has no root to reach it
/// from. Each process left unreached is made a root, cut from the parent that closed the cycle.</para>
/// </summary>
internal sealed class ProcessForest
{
    private static readonly IReadOnlyList<ProcessMemory> NoChildren = [];

    private readonly Dictionary<ProcessMemory, List<ProcessMemory>> _children;

    private ProcessForest(List<ProcessMemory> roots, Dictionary<ProcessMemory, List<ProcessMemory>> children)
    {
        Roots = roots;
        _children = children;
    }

    public IReadOnlyList<ProcessMemory> Roots { get; }

    public IReadOnlyList<ProcessMemory> ChildrenOf(ProcessMemory process) =>
        _children.TryGetValue(process, out var children) ? children : NoChildren;

    /// <param name="processes">Every process to place, each with a creation time.</param>
    /// <param name="alwaysRoots">
    /// Identifiers placed at a root whatever their parent, which is how every service host comes to sit
    /// at the top of its part.
    /// </param>
    public static ProcessForest Of(IReadOnlyList<ProcessMemory> processes, IReadOnlySet<int> alwaysRoots)
    {
        var byId = new Dictionary<int, ProcessMemory>(processes.Count);

        foreach (var process in processes)
        {
            byId.TryAdd(process.ProcessId, process);
        }

        // By reference: two records can compare equal, and each is still a process of its own.
        var children = new Dictionary<ProcessMemory, List<ProcessMemory>>(ReferenceEqualityComparer.Instance);
        var parents = new Dictionary<ProcessMemory, ProcessMemory>(ReferenceEqualityComparer.Instance);

        foreach (var process in processes)
        {
            if (ParentOf(process, byId, alwaysRoots) is { } parent)
            {
                parents[process] = parent;

                if (!children.TryGetValue(parent, out var siblings))
                {
                    children[parent] = siblings = [];
                }

                siblings.Add(process);
            }
        }

        var roots = new List<ProcessMemory>();
        var reached = new HashSet<ProcessMemory>(ReferenceEqualityComparer.Instance);

        foreach (var process in processes)
        {
            if (!parents.ContainsKey(process))
            {
                roots.Add(process);
                Reach(process, children, reached);
            }
        }

        foreach (var process in processes)
        {
            if (!reached.Contains(process))
            {
                children[parents[process]].Remove(process);
                roots.Add(process);
                Reach(process, children, reached);
            }
        }

        return new ProcessForest(roots, children);
    }

    private static ProcessMemory? ParentOf(
        ProcessMemory process, Dictionary<int, ProcessMemory> byId, IReadOnlySet<int> alwaysRoots)
    {
        if (alwaysRoots.Contains(process.ProcessId)
            || !byId.TryGetValue(process.ParentProcessId, out var parent)
            || ReferenceEquals(parent, process))
        {
            return null;
        }

        return parent.CreationTime <= process.CreationTime ? parent : null;
    }

    /// <summary>Mark everything under <paramref name="from"/> reached, without recursion.</summary>
    private static void Reach(
        ProcessMemory from, Dictionary<ProcessMemory, List<ProcessMemory>> children, HashSet<ProcessMemory> reached)
    {
        var pending = new Stack<ProcessMemory>();
        pending.Push(from);

        while (pending.TryPop(out var process))
        {
            if (!reached.Add(process) || !children.TryGetValue(process, out var below))
            {
                continue;
            }

            foreach (var child in below)
            {
                pending.Push(child);
            }
        }
    }
}
