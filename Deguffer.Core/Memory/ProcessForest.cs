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
/// <para><b>A service host is never in a tree of processes.</b> It is a root whatever started it, and
/// nothing it starts sits under it. Windows starts packaged applications, brokers and COM servers from
/// a service host, and drawing them under it would put applications inside Services and add their
/// memory to services that do not hold it.</para>
///
/// <para><b>No process is placed twice, and none is lost.</b> Links that the creation times allow can
/// still form a cycle where processes share a creation time, and a cycle has no root to reach it from.
/// Each cycle is opened at a process on it, which is found by walking up from anything the roots did
/// not reach, so what hangs below the cycle stays where its own parent put it.</para>
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
    /// <param name="hosts">The identifiers of the processes that host a service.</param>
    public static ProcessForest Of(IReadOnlyList<ProcessMemory> processes, IReadOnlySet<int> hosts)
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
            if (ParentOf(process, byId, hosts) is { } parent)
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
            if (reached.Contains(process))
            {
                continue;
            }

            var member = OnTheCycleAbove(process, parents);
            var siblings = children[parents[member]];

            siblings.RemoveAt(siblings.FindIndex(sibling => ReferenceEquals(sibling, member)));
            parents.Remove(member);
            roots.Add(member);
            Reach(member, children, reached);
        }

        return new ProcessForest(roots, children);
    }

    private static ProcessMemory? ParentOf(
        ProcessMemory process, Dictionary<int, ProcessMemory> byId, IReadOnlySet<int> hosts)
    {
        if (hosts.Contains(process.ProcessId)
            || hosts.Contains(process.ParentProcessId)
            || !byId.TryGetValue(process.ParentProcessId, out var parent)
            || ReferenceEquals(parent, process))
        {
            return null;
        }

        return parent.CreationTime <= process.CreationTime ? parent : null;
    }

    /// <summary>
    /// The first process met twice walking up from <paramref name="process"/>, which is a process on
    /// the cycle.
    ///
    /// <para>The walk ends: <paramref name="process"/> was not reached from any root, so nothing above it
    /// was either, and every process no root reached has a parent.</para>
    /// </summary>
    private static ProcessMemory OnTheCycleAbove(ProcessMemory process, Dictionary<ProcessMemory, ProcessMemory> parents)
    {
        var seen = new HashSet<ProcessMemory>(ReferenceEqualityComparer.Instance);
        var current = process;

        while (seen.Add(current))
        {
            current = parents[current];
        }

        return current;
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
