namespace Deguffer.Core.Exploring;

/// <summary>One entry to record, as the thing that found it saw it.</summary>
/// <param name="Name">The leaf name, with no path.</param>
/// <param name="Size">
/// Bytes, for a file. Zero for a directory: a directory's own bytes are its children's, and they
/// are recorded separately, so anything counted here would be counted twice.
/// </param>
/// <param name="Created">When the entry was made, or unknown where nothing established it.</param>
/// <param name="LastWritten">
/// When the entry itself was last written. A directory's own timestamp is meant here, not its
/// contents' — <see cref="ExploreTree.ModifiedOf"/> is what combines the two, and it can only do
/// that if what it is given is the node's own.
/// </param>
public readonly record struct ExploreChild(
    string Name,
    bool IsDirectory,
    bool IsLink,
    long Size,
    ExploreTimestamp Created = default,
    ExploreTimestamp LastWritten = default);

/// <summary>
/// Accumulates nodes as something discovers them, then hands over the finished
/// <see cref="ExploreTree"/>.
///
/// <para>Separate from the readers that fill it because gathering and assembling are different
/// jobs (G1): a reader knows how to get entries out of a volume, and this knows how to keep
/// millions of them cheaply while several threads produce them at once.</para>
/// </summary>
public sealed class ExploreTreeBuilder
{
    /// <summary>The node the scan started from. Always zero here, unlike the file table's root.</summary>
    public const int RootNode = 0;

    private readonly string _rootPath;
    private readonly List<string> _names;
    private readonly List<int> _parents;
    private readonly List<long> _sizes;
    private readonly List<bool> _isDirectory;
    private readonly List<bool> _isLink;
    private readonly List<bool> _sizeUnknown;
    private readonly List<ExploreTimestamp> _created;
    private readonly List<ExploreTimestamp> _modified;
    private readonly Lock _gate = new();

    /// <param name="created">
    /// When the root directory itself was made, where the caller knows. The root is the one node
    /// nothing discovers as a child, so it is the one node whose dates have to be handed in rather
    /// than found — and unknown is the honest default for a caller that did not look.
    /// </param>
    /// <param name="lastWritten">
    /// The root's own last-written time. Only ever a floor under what <see cref="Build"/> reports
    /// for it, because <see cref="ExploreTree.ModifiedOf"/> rolls every child's up into it.
    /// </param>
    public ExploreTreeBuilder(
        string rootPath,
        ExploreTimestamp created = default,
        ExploreTimestamp lastWritten = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _rootPath = rootPath;

        // The root is node zero and its own parent, which is what stops the link inversion in
        // ExploreTree.Create from making the tree cyclic.
        _names = [rootPath];
        _parents = [RootNode];
        _sizes = [0];
        _isDirectory = [true];
        _isLink = [false];
        _sizeUnknown = [false];
        _created = [created];
        _modified = [lastWritten];
    }

    /// <summary>
    /// Record every child of <paramref name="parent"/> at once, and return the node number of the
    /// first. The rest follow it consecutively, which is how a caller learns the number of a child
    /// directory it wants to descend into without this having to hand back a list.
    ///
    /// <para>A whole directory per call, rather than an entry per call, because the lock is the
    /// cost. A directory holds tens to thousands of entries and a volume holds hundreds of
    /// thousands of directories, so batching turns one contended acquisition per file into one per
    /// directory (G4).</para>
    /// </summary>
    public int AddChildren(int parent, IReadOnlyList<ExploreChild> children)
    {
        ArgumentNullException.ThrowIfNull(children);

        lock (_gate)
        {
            var first = _names.Count;

            foreach (var child in children)
            {
                _names.Add(child.Name);
                _parents.Add(parent);
                _sizes.Add(child.Size);
                _isDirectory.Add(child.IsDirectory);
                _isLink.Add(child.IsLink);
                _sizeUnknown.Add(false);
                _created.Add(child.Created);
                _modified.Add(child.LastWritten);
            }

            return first;
        }
    }

    /// <summary>
    /// Say that this node's size cannot be established, so every total above it is a lower bound.
    ///
    /// <para>The caller is the walk meeting a directory it was refused. §5.3 makes that ordinary and
    /// the walk still skips it, but the bytes behind it are real and unmeasured — reporting the
    /// total as though they were not there is the one thing a size picture must not do.</para>
    /// </summary>
    public void MarkSizeUnknown(int node)
    {
        lock (_gate)
        {
            _sizeUnknown[node] = true;
        }
    }

    /// <summary>
    /// The tree as it stands, ordering each node's children by <paramref name="childOrder"/>.
    ///
    /// <para>Callable while the walk is still running, which is what makes a snapshot possible, and
    /// it is the snapshot that wants an order other than by size — see
    /// <see cref="ExploreChildOrder"/>. Node numbers are indices into these lists and the lists only
    /// ever grow, so a node in one snapshot is the same node in the next and in the finished
    /// tree.</para>
    ///
    /// <para>Every slot was filled by this builder, so every one is present — the distinction
    /// <see cref="ExploreTree.Create"/> needs exists for the file table, which leaves free and
    /// unreadable records empty.</para>
    /// </summary>
    public ExploreTree Build(ExploreChildOrder childOrder)
    {
        lock (_gate)
        {
            var present = new bool[_names.Count];
            Array.Fill(present, true);

            return ExploreTree.Create(
                _rootPath,
                RootNode,
                [.. _names],
                [.. _parents],
                [.. _sizes],
                [.. _isDirectory],
                [.. _isLink],
                [.. _sizeUnknown],
                [.. _created],
                [.. _modified],
                present,
                childOrder);
        }
    }
}
