namespace Deguffer.Core.Memory;

/// <summary>
/// The nodes of a <see cref="MemoryTree"/> as <see cref="MemoryTreeBuilder"/> adds them, and the one
/// pass that turns them into the tree: totalling each subtree, and ordering every node's children.
///
/// <para>Separate from the builder (G1): the builder knows what the parts of memory are, and this knows
/// how a tree of them is stored.</para>
/// </summary>
internal sealed class TreeDrafts
{
    private static readonly IReadOnlyList<RunningService> NoServices = [];

    private readonly List<string> _names = [];
    private readonly List<MemoryPart> _parts = [];
    private readonly List<int> _parents = [];
    private readonly List<long> _bytes = [];
    private readonly List<ProcessMemory?> _processes = [];
    private readonly List<IReadOnlyList<RunningService>> _services = [];

    /// <summary>Every byte added so far, which is what the remainder is measured against.</summary>
    public long Attributed { get; private set; }

    /// <summary>How far the parts add up to more than physical memory. See <see cref="MemoryTree.Overcount"/>.</summary>
    public long Overcount { get; set; }

    /// <summary>
    /// Add a node holding <paramref name="bytes"/> of its own under <paramref name="parent"/>, and return
    /// its number. A parent is always added before its children, which is what lets
    /// <see cref="Finish"/> total the tree in one backward pass.
    /// </summary>
    public int Add(
        string name,
        MemoryPart part,
        int parent,
        long bytes,
        ProcessMemory? process = null,
        IReadOnlyList<RunningService>? services = null)
    {
        _names.Add(name);
        _parts.Add(part);
        _parents.Add(parent);
        _bytes.Add(bytes);
        _processes.Add(process);
        _services.Add(services is { Count: > 0 } ? services : NoServices);

        Attributed += bytes;

        return _names.Count - 1;
    }

    public MemoryTree Finish(MemorySnapshot snapshot)
    {
        var count = _names.Count;
        var names = _names.ToArray();
        var parents = _parents.ToArray();
        var sizes = _bytes.ToArray();
        var processes = _processes.ToArray();

        // Backwards, so every child is total before its parent is reached. Node zero is the root and its
        // own parent, so it is left out.
        for (var node = count - 1; node > 0; node--)
        {
            sizes[parents[node]] += sizes[node];
        }

        var childStart = new int[count + 1];

        for (var node = 1; node < count; node++)
        {
            childStart[parents[node] + 1]++;
        }

        for (var node = 0; node < count; node++)
        {
            childStart[node + 1] += childStart[node];
        }

        var children = new int[count - 1];
        var cursor = new int[count];

        for (var node = 1; node < count; node++)
        {
            var parent = parents[node];
            children[childStart[parent] + cursor[parent]++] = node;
        }

        var order = BySize(sizes, names, processes);

        for (var node = 0; node < count; node++)
        {
            children.AsSpan(childStart[node], childStart[node + 1] - childStart[node]).Sort(order);
        }

        return new MemoryTree(
            snapshot, Overcount, names, [.. _parts], parents, sizes, childStart, children, processes, [.. _services]);
    }

    /// <summary>
    /// Largest first, which the treemap and the sunburst require. Ties go by name, then by process
    /// identifier, then by node number, so the order is total and two refreshes of an unchanged machine
    /// draw the same picture.
    /// </summary>
    private static Comparison<int> BySize(long[] sizes, string[] names, ProcessMemory?[] processes) =>
        (left, right) =>
        {
            var bySize = sizes[right].CompareTo(sizes[left]);

            if (bySize != 0)
            {
                return bySize;
            }

            var byName = string.Compare(names[left], names[right], StringComparison.OrdinalIgnoreCase);

            if (byName != 0)
            {
                return byName;
            }

            var byProcess = (processes[left]?.ProcessId ?? -1).CompareTo(processes[right]?.ProcessId ?? -1);

            return byProcess != 0 ? byProcess : left.CompareTo(right);
        };
}
