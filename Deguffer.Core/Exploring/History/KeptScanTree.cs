using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.History;

/// <summary>
/// The last kept scan of a drive as a tree the layouts can draw, for the map to show while a new scan
/// of the drive runs (#491). See <see cref="KeptScanShown"/> for when the page shows it.
///
/// <para><b>Built from what the summary recorded</b>, which is the drive's largest folders and every
/// folder above them (<see cref="ScanSummary"/>). The rest of what a recorded folder held, its files
/// and its folders too small to record, is one block under it, labelled as the replay of what grew
/// labels it (<see cref="GrowthText.Unrecorded"/>). The node of a recorded folder is its place in
/// <see cref="ScanSummary.Folders"/>, so the root is node 0, and each block is numbered after every
/// folder.</para>
///
/// <para><b>A picture only</b> (§7.1). It shows the drive as it was when that scan finished, not as it
/// is, so nothing drawn of it answers the pointer and nothing in it can be picked, opened or removed:
/// see <see cref="Rendering.MapReading.Answers"/>. The removal verdict and the remover read the tree
/// a scan built and the disk, never a summary.</para>
///
/// <para>Everything is worked out as it is built and only read afterwards, so a layout on a worker and
/// the page on the UI thread can read one at once. A summary holds at most
/// <see cref="ScanSummaries.FolderLimit"/> folders, so that is a single pass over a few thousand
/// entries, made once for each scan that shows it.</para>
/// </summary>
public sealed class KeptScanTree : ITrackedTree
{
    private readonly long[] _sizes;

    private readonly int[] _parents;

    private readonly int[][] _children;

    /// <summary>
    /// The recorded folders under each recorded folder that has any, by name without regard to case,
    /// for <see cref="CounterpartsFrom"/>. Read by span, so a lookup makes no string.
    /// </summary>
    private readonly Dictionary<int, Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>>> _byName = [];

    private KeptScanTree(ScanSummary summary, string rootPath)
    {
        Summary = summary;
        RootPath = rootPath;

        var folders = summary.Folders;
        var recordedBelow = new long[folders.Count];

        for (var i = 1; i < folders.Count; i++)
        {
            recordedBelow[folders[i].Parent] += folders[i].Bytes;
        }

        var parents = new List<int>(folders.Count);
        var sizes = new List<long>(folders.Count);

        for (var i = 0; i < folders.Count; i++)
        {
            parents.Add(i == 0 ? 0 : folders[i].Parent);
            sizes.Add(folders[i].Bytes);
        }

        for (var i = 0; i < folders.Count; i++)
        {
            if (folders[i].Bytes - recordedBelow[i] is > 0 and var unrecorded)
            {
                parents.Add(i);
                sizes.Add(unrecorded);
            }
        }

        _parents = [.. parents];
        _sizes = [.. sizes];

        var counts = new int[_parents.Length];

        for (var node = 1; node < _parents.Length; node++)
        {
            counts[_parents[node]]++;
        }

        _children = new int[_parents.Length][];

        for (var node = 0; node < _parents.Length; node++)
        {
            _children[node] = counts[node] == 0 ? [] : new int[counts[node]];
            counts[node] = 0;
        }

        for (var node = 1; node < _parents.Length; node++)
        {
            var parent = _parents[node];
            _children[parent][counts[parent]++] = node;
        }

        Comparison<int> largestFirst = CompareBySize;

        foreach (var siblings in _children)
        {
            siblings.AsSpan().Sort(largestFirst);
        }

        for (var i = 1; i < folders.Count; i++)
        {
            if (!_byName.TryGetValue(folders[i].Parent, out var names))
            {
                names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();
                _byName[folders[i].Parent] = names;
            }

            names.Dictionary.TryAdd(folders[i].Name, i);
        }
    }

    /// <summary>
    /// The tree <paramref name="summary"/> draws as, for a scan of its volume at
    /// <paramref name="rootPath"/>: where the volume is mounted now, which can be another letter than
    /// the one it was scanned under. Null for a summary that recorded no folder, not even its root,
    /// which has nothing to draw.
    /// </summary>
    public static KeptScanTree? Of(ScanSummary summary, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        return summary.Folders.Count == 0 ? null : new KeptScanTree(summary, rootPath);
    }

    /// <summary>The kept scan this draws.</summary>
    public ScanSummary Summary { get; }

    /// <summary>Where the volume is mounted now, which the root is named by and a scan of it is rooted at.</summary>
    public string RootPath { get; }

    /// <summary>
    /// The volume to draw beside the root: its capacity and free space when the kept scan finished,
    /// as the folders are. What Windows stated about its hidden space was not kept, so the space no
    /// folder accounts for is one block, as <see cref="GrowthReplay.VolumeAt"/> draws an earlier scan.
    /// </summary>
    public VolumeSpace Volume => new(Summary.TotalBytes, Summary.FreeBytes);

    public int RootNode => 0;

    public ExploreChildOrder ChildOrder => ExploreChildOrder.BySize;

    /// <summary>How many nodes this numbers: every recorded folder, then each block of what one did not record.</summary>
    public int NodeCount => _parents.Length;

    public long SizeOf(int node) => _sizes[node];

    public ReadOnlySpan<int> ChildrenOf(int node) => _children[node];

    /// <summary>A recorded folder is a container. A block of what one did not record is not, as nothing in it was recorded.</summary>
    public bool IsContainer(int node) => node < Summary.Folders.Count;

    public int ParentOf(int node) => _parents[node];

    /// <summary>
    /// A node's name, or null for a block of what the kept scan did not record. The root is named by
    /// <see cref="RootPath"/>, as a scanned tree's root is, rather than by the path it was scanned at.
    /// </summary>
    public string? NameOf(int node) =>
        node == RootNode ? RootPath
        : node < Summary.Folders.Count ? Summary.Folders[node].Name
        : null;

    /// <summary>
    /// What a shape is labelled: its name and what the kept scan counted at or below it, or what a
    /// block of what it did not record stands for. The page states that the whole map is that scan.
    /// </summary>
    public MapCaption Caption(int node) => MapShapeText.Caption(
        NameOf(node) ?? GrowthText.Unrecorded(Summary.UnrecordedAtMost),
        SizeOf(node));

    /// <summary>
    /// Find each recorded folder in a scan of the same volume, by name, folder under folder, from the
    /// root down, as <see cref="ScanGrowth"/> matches a summary: the root is the volume's whatever
    /// letter it wears now (<see cref="RootPath"/>). A block of what was not recorded has no
    /// counterpart, and a tree rooted anywhere else is of something else.
    /// </summary>
    public Func<int, int?>? CounterpartsIn(ISizedTree later)
    {
        if (later is not ExploreTree scanned || !IsOf(scanned))
        {
            return null;
        }

        var found = new Dictionary<int, int?> { [RootNode] = scanned.RootNode };
        var names = new Dictionary<int, ChildrenByName>();

        int? Find(int node)
        {
            if (found.TryGetValue(node, out var known))
            {
                return known;
            }

            int? there = null;

            if (node < Summary.Folders.Count && Find(_parents[node]) is { } parent)
            {
                if (!names.TryGetValue(parent, out var children))
                {
                    children = new ChildrenByName(scanned);

                    foreach (var child in scanned.ChildrenOf(parent))
                    {
                        children.Add(child);
                    }

                    names[parent] = children;
                }

                there = children.Find(Summary.Folders[node].Name);
            }

            found[node] = there;

            return there;
        }

        return Find;
    }

    /// <summary>Whether <paramref name="scanned"/> is a scan rooted where this tree is: see <see cref="RootPath"/>.</summary>
    internal bool IsOf(ExploreTree scanned) =>
        string.Equals(scanned.RootPath, RootPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The way back from <see cref="CounterpartsIn"/>: each node of <paramref name="earlier"/>, a scan
    /// rooted where this tree is (<see cref="IsOf"/>), as this tree numbers it, or null where the kept
    /// scan did not record it.
    /// </summary>
    internal Func<int, int?> CounterpartsFrom(ExploreTree earlier)
    {
        var found = new Dictionary<int, int?> { [earlier.RootNode] = RootNode };

        int? Find(int node)
        {
            if (found.TryGetValue(node, out var known))
            {
                return known;
            }

            var parent = earlier.ParentOf(node);

            // A node with no parent other than itself and not the root is one the tree cannot place.
            var there = parent != node && Find(parent) is { } folder ? Recorded(folder, earlier.NameSpanOf(node)) : null;

            found[node] = there;

            return there;
        }

        return Find;
    }

    /// <summary>
    /// The recorded folder called <paramref name="name"/> under <paramref name="folder"/>, or null. Of
    /// two recorded names that differ only in case, which a directory with case sensitivity turned on
    /// can hold, the one spelt exactly as asked, as <see cref="ChildrenByName"/> finds it.
    /// </summary>
    private int? Recorded(int folder, ReadOnlySpan<char> name)
    {
        if (!_byName.TryGetValue(folder, out var names) || !names.TryGetValue(name, out var recorded))
        {
            return null;
        }

        if (!Summary.Folders[recorded].Name.AsSpan().SequenceEqual(name))
        {
            foreach (var child in _children[folder])
            {
                if (NameOf(child) is { } other && other.AsSpan().SequenceEqual(name))
                {
                    return child;
                }
            }
        }

        return recorded;
    }

    /// <summary>
    /// Larger first, and siblings of one size by name, so the order does not turn on how the summary
    /// happened to list them. A block of what was not recorded comes before its namesakes, as in a
    /// replay (<see cref="GrowthReplay.CompareByName"/>).
    /// </summary>
    private int CompareBySize(int left, int right)
    {
        var bySize = _sizes[right].CompareTo(_sizes[left]);

        if (bySize != 0)
        {
            return bySize;
        }

        var (leftName, rightName) = (NameOf(left), NameOf(right));

        return leftName is not null && rightName is not null
            ? string.Compare(leftName, rightName, StringComparison.OrdinalIgnoreCase)
            : (leftName is not null).CompareTo(rightName is not null);
    }
}
