using System.Buffers;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>One entry to record, as the thing that found it saw it.</summary>
/// <param name="Name">The leaf name, with no path.</param>
/// <param name="Size">
/// The bytes a file occupies on the disk, which is what the picture draws. Zero for a directory: a
/// directory's own bytes are its children's, and they are recorded separately, so anything counted
/// here would be counted twice.
/// </param>
/// <param name="Created">When the entry was made, or unknown where nothing established it.</param>
/// <param name="LastWritten">
/// When the entry itself was last written. A directory's own timestamp is meant here, not its
/// contents' — <see cref="ExploreTree.ModifiedOf"/> is what combines the two, and it can only do
/// that if what it is given is the node's own.
/// </param>
/// <param name="Length">
/// The file's length, where it is not <paramref name="Size"/>. Null where the two are one figure,
/// which is every file a listing alone describes.
/// </param>
/// <param name="Storage">Why <paramref name="Size"/> is less than the length, where anything says.</param>
/// <param name="SizeUnknown">
/// Whether the file system would not say what the file occupies, so <paramref name="Size"/> is a
/// lower bound and so is every total above it.
/// </param>
/// <param name="Visibility">Whether the entry is hidden or a system file, as its attributes say.</param>
public readonly record struct ExploreChild(
    string Name,
    bool IsDirectory,
    bool IsLink,
    long Size,
    ExploreTimestamp Created = default,
    ExploreTimestamp LastWritten = default,
    long? Length = null,
    FileStorage Storage = FileStorage.Plain,
    bool SizeUnknown = false,
    FileVisibility Visibility = FileVisibility.Shown);

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

    // One record per node rather than a list per column. A snapshot reads what is published here
    // without the lock (see Capture), which a List cannot allow, because it moves its array to grow.
    private readonly AppendOnlyChunks<Node> _nodes = new();

    // Every name recorded, each distinct one once, written under the lock like the nodes that number
    // them.
    private readonly NamePool _names = new();

    // Marks made on a node after it was added, kept apart from it so that what a capture reads is
    // never written again. A refusal is rare against the nodes, so copying these under the lock
    // costs next to nothing.
    private readonly List<int> _sizeUnknown = [];
    private readonly List<int> _listingRefused = [];

    // Each parent handed children in more than one call, whose children are not in one batch's name
    // order. The walk lists each folder once, so a walk never adds to this.
    private readonly HashSet<int> _listedTwice = [];

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
        _nodes.Add() = new Node
        {
            Name = _names.Intern(rootPath),
            Parent = RootNode,
            IsDirectory = true,
            Created = created,
            Modified = lastWritten,
        };
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
    ///
    /// <para>The children are put in name order here, once and before the lock is taken, rather
    /// than in every snapshot. A directory's children arrive together and never change, so their
    /// order is known as soon as they are, and a snapshot places each one where its rank says
    /// without sorting anything.</para>
    ///
    /// <para>A span, so the walk can hand over a buffer it reuses rather than a list per directory.
    /// What it holds is copied before this returns, so the caller may reuse it straight away.</para>
    /// </summary>
    public int AddChildren(int parent, ReadOnlySpan<ExploreChild> children)
    {
        var rented = ArrayPool<NameKey>.Shared.Rent(children.Length);
        var byName = rented.AsSpan(0, children.Length);

        try
        {
            for (var i = 0; i < byName.Length; i++)
            {
                byName[i] = new NameKey(children[i].Name, i);
            }

            // The keys carry their names, so the comparison captures nothing and the sort allocates
            // nothing. Ties go by index within the batch, which is the tree's own tie-break: a batch
            // is numbered consecutively, so index order is node-number order.
            byName.Sort(static (left, right) =>
                ExploreTree.CompareByName(left.Name, left.Index, right.Name, right.Index));

            lock (_gate)
            {
                var first = _nodes.Count;

                ref var listed = ref _nodes[parent];
                if (listed.HasChildren)
                {
                    _listedTwice.Add(parent);
                }

                listed.HasChildren = true;

                foreach (ref readonly var child in children)
                {
                    _nodes.Add() = new Node
                    {
                        Name = _names.Intern(child.Name),
                        Parent = parent,
                        Size = child.Size,
                        Length = child.Length ?? child.Size,
                        Storage = child.Storage,
                        IsDirectory = child.IsDirectory,
                        IsLink = child.IsLink,
                        SizeUnknown = child.SizeUnknown,
                        Created = child.Created,
                        Modified = child.LastWritten,
                        Visibility = child.Visibility,
                    };
                }

                for (var rank = 0; rank < byName.Length; rank++)
                {
                    _nodes[first + byName[rank].Index].NameRank = rank;
                }

                return first;
            }
        }
        finally
        {
            // Cleared, because a key holds its name and the pool would otherwise keep it alive.
            byName.Clear();
            ArrayPool<NameKey>.Shared.Return(rented);
        }
    }

    /// <summary>One child's name and its index in the batch, which is what the name sort compares.</summary>
    private readonly record struct NameKey(string Name, int Index);

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
            ThrowIfNotAdded(node);
            _sizeUnknown.Add(node);
        }
    }

    /// <summary>
    /// Say that the walk was refused a listing of this folder: its size is unknown, as
    /// <see cref="MarkSizeUnknown"/> says, and the tree names the folder
    /// (<see cref="ExploreTree.ListingWasRefused"/>).
    ///
    /// <para>Named as well as sized, because a caller going through the files cannot tell a folder
    /// it was refused from an empty one by its size, and a duplicate search that read the one as
    /// the other would report a search that skipped a place as one that found nothing there.</para>
    /// </summary>
    public void MarkListingRefused(int node)
    {
        lock (_gate)
        {
            ThrowIfNotAdded(node);
            _sizeUnknown.Add(node);
            _listingRefused.Add(node);
        }
    }

    // A mark is applied only when a tree is assembled, so a node that was never added is refused
    // here, where the caller made the mistake, rather than in a snapshot on another thread.
    private void ThrowIfNotAdded(int node)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(node);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(node, _nodes.Count);
    }

    /// <summary>
    /// The tree as it stands, ordering each node's children by <paramref name="childOrder"/>.
    ///
    /// <para>Callable while the walk is still running, which is what makes a snapshot possible, and
    /// it is the snapshot that wants an order other than by size — see
    /// <see cref="ExploreChildOrder"/>. Node numbers are positions in what this holds, which only
    /// ever grows, so a node in one snapshot is the same node in the next and in the finished
    /// tree.</para>
    /// </summary>
    public ExploreTree Build(ExploreChildOrder childOrder) => Capture().Build(childOrder);

    /// <summary>
    /// What has been recorded so far, to be assembled into a tree once the lock is released.
    ///
    /// <para>All the lock covers is a count and the few marks made since nodes were added.
    /// Assembling a tree is several passes over every node, and while the lock was held for them
    /// every walk worker that finished a listing waited for them to end.</para>
    /// </summary>
    internal ExploreTreeCapture Capture()
    {
        lock (_gate)
        {
            return new ExploreTreeCapture(
                _rootPath, _nodes.Freeze(), _names.Freeze(), [.. _sizeUnknown], [.. _listingRefused], [.. _listedTwice]);
        }
    }

    /// <summary>
    /// One node as it was recorded. Nothing but <see cref="HasChildren"/> and, in the call that adds
    /// the node, <see cref="NameRank"/> is written after it is added, which is what lets a capture
    /// read the rest without the lock.
    /// </summary>
    internal struct Node
    {
        /// <summary>The node's name, as numbered in the builder's <see cref="NamePool"/>.</summary>
        public int Name;
        public int Parent;

        /// <summary>Where the node falls by name among the children recorded with it.</summary>
        public int NameRank;

        public long Size;
        public long Length;
        public ExploreTimestamp Created;
        public ExploreTimestamp Modified;
        public FileStorage Storage;
        public FileVisibility Visibility;
        public bool IsDirectory;
        public bool IsLink;
        public bool SizeUnknown;

        /// <summary>
        /// Whether children have been recorded under this node. Read and written only under the
        /// builder's lock, and never read by a capture.
        /// </summary>
        public bool HasChildren;
    }
}
