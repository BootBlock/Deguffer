using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Memory;

/// <summary>
/// Where physical memory is, as a tree sized by bytes: <see cref="MemoryPart.Applications"/>,
/// <see cref="MemoryPart.Services"/> and <see cref="MemoryPart.Windows"/> under the root, built from one
/// <see cref="MemorySnapshot"/> by <see cref="MemoryTreeBuilder"/>.
///
/// <para>Parallel arrays indexed by node, as <see cref="ExploreTree"/> is, so the layouts that draw a
/// disk tree can draw this one by the same four questions: how big a node is, what it holds, whether
/// it holds anything, and in what order.</para>
///
/// <para>A node number means something only in the tree that gave it. A refresh builds a new tree, and
/// <see cref="MemoryPlace"/> is what finds the same process or part in it.</para>
/// </summary>
public sealed class MemoryTree : ISizedTree
{
    private readonly string[] _names;
    private readonly MemoryPart[] _parts;
    private readonly int[] _parents;
    private readonly long[] _sizes;
    private readonly int[] _childStart;
    private readonly int[] _children;
    private readonly ProcessMemory?[] _processes;
    private readonly IReadOnlyList<RunningService>[] _services;
    private readonly Dictionary<MemoryNodeKey, int> _index;

    internal MemoryTree(
        MemorySnapshot snapshot,
        long overcount,
        string[] names,
        MemoryPart[] parts,
        int[] parents,
        long[] sizes,
        int[] childStart,
        int[] children,
        ProcessMemory?[] processes,
        IReadOnlyList<RunningService>[] services)
    {
        Snapshot = snapshot;
        Overcount = overcount;
        _names = names;
        _parts = parts;
        _parents = parents;
        _sizes = sizes;
        _childStart = childStart;
        _children = children;
        _processes = processes;
        _services = services;
        _index = new Dictionary<MemoryNodeKey, int>(names.Length);

        for (var node = 0; node < names.Length; node++)
        {
            _index.TryAdd(KeyOf(node), node);
        }
    }

    /// <summary>What the tree was built from, for the figures a picture of it states beside it.</summary>
    public MemorySnapshot Snapshot { get; }

    /// <summary>
    /// How far the drawn parts add up to more than physical memory, or zero where they do not.
    ///
    /// <para>The figures come from separate calls a moment apart, so they can overlap by what moved in
    /// between. The remainder is then drawn as nothing rather than as less than nothing, and this is
    /// the amount a view states instead (§7.2).</para>
    /// </summary>
    public long Overcount { get; }

    public int RootNode => 0;

    /// <summary>Always by size, largest first: a memory tree is only ever built whole.</summary>
    public ExploreChildOrder ChildOrder => ExploreChildOrder.BySize;

    public int NodeCount => _names.Length;

    public long SizeOf(int node) => _sizes[node];

    public ReadOnlySpan<int> ChildrenOf(int node) =>
        _children.AsSpan(_childStart[node], _childStart[node + 1] - _childStart[node]);

    public bool IsContainer(int node) => _childStart[node + 1] > _childStart[node];

    /// <summary>The node above <paramref name="node"/>. The root is its own parent.</summary>
    public int ParentOf(int node) => _parents[node];

    /// <summary>A part's name, or a process's image name.</summary>
    public string NameOf(int node) => _names[node];

    public MemoryPart PartOf(int node) => _parts[node];

    /// <summary>
    /// The process behind a <see cref="MemoryPart.Process"/>, <see cref="MemoryPart.OwnShare"/> or
    /// <see cref="MemoryPart.CompressionStore"/> node, and null for any other node.
    /// </summary>
    public ProcessMemory? ProcessOf(int node) => _processes[node];

    /// <summary>The services the process behind this node hosts, and empty where it hosts none or the node is not a process.</summary>
    public IReadOnlyList<RunningService> ServicesOf(int node) => _services[node];

    /// <summary>
    /// What <paramref name="node"/> is. A process and its own share by the process's identifier and
    /// creation time. Everything else by its part alone, the compression store included: it is a part of
    /// Windows that happens to be held by a process, and it is the same part whichever process holds it.
    /// </summary>
    public MemoryNodeKey KeyOf(int node) =>
        _parts[node] is MemoryPart.Process or MemoryPart.OwnShare
        && _processes[node] is { CreationTime: { } created } process
            ? new MemoryNodeKey(_parts[node], process.ProcessId, created)
            : MemoryNodeKey.Of(_parts[node]);

    /// <summary>The node <paramref name="key"/> names in this tree, or null where this tree has none.</summary>
    public int? Find(MemoryNodeKey key) => _index.TryGetValue(key, out var node) ? node : null;
}
