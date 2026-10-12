using System.Collections.Frozen;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// A scanned tree of directories and files, as parallel arrays indexed by node.
///
/// <para>Parallel arrays for the reason <see cref="Scanning.Mft.MftVolumeTree"/> uses them: a real
/// volume runs to millions of entries, and one object per node would cost more in object headers
/// than every field here put together.</para>
///
/// <para>This holds no knowledge of drawing, of tiers, or of what may be deleted. It answers "what
/// is under this node, how big is it, and when was it touched", and nothing else — which is what
/// lets one tree serve a treemap, a sunburst and a list without any of them being privileged.</para>
///
/// <para>It is drawn through <see cref="Layout.ISizedTree"/>, which is the part of it a layout reads.
/// The other tree behind that seam is <see cref="Memory.MemoryTree"/>.</para>
///
/// <para>One file past the usual length because every member reads the same private arrays, and
/// building them is the three passes that make them mean anything. What is taken out after a
/// removal is its own type, <see cref="Exploring.RemovedSinceScan"/>, which this only consults.</para>
/// </summary>
public sealed class ExploreTree : Layout.ITrackedTree
{
    private readonly string[] _names;
    private readonly int[] _parents;
    private readonly long[] _sizes;
    private readonly long[] _lengths;
    private readonly FileStorage[] _storage;
    private readonly bool[] _isDirectory;
    private readonly bool[] _isLink;
    private readonly bool[] _sizeUnknown;
    private readonly ExploreTimestamp[] _created;
    private readonly ExploreTimestamp[] _modified;
    private readonly FileVisibility[] _visibility;
    private readonly int[] _childStart;
    private readonly int[] _children;
    private readonly bool[] _placed;
    private readonly FrozenSet<int> _listingRefused;

    /// <summary>What has been taken out since the scan, or null for the tree as the scan found it.</summary>
    private readonly RemovedSinceScan? _removed;

    private ExploreTree(
        string rootPath,
        int rootNode,
        string[] names,
        int[] parents,
        long[] sizes,
        long[] lengths,
        FileStorage[] storage,
        bool[] isDirectory,
        bool[] isLink,
        bool[] sizeUnknown,
        ExploreTimestamp[] created,
        ExploreTimestamp[] modified,
        FileVisibility[] visibility,
        int[] childStart,
        int[] children,
        bool[] placed,
        ExploreChildOrder childOrder,
        FrozenSet<int> listingRefused)
    {
        RootPath = rootPath;
        RootNode = rootNode;
        ChildOrder = childOrder;
        _names = names;
        _parents = parents;
        _sizes = sizes;
        _lengths = lengths;
        _storage = storage;
        _isDirectory = isDirectory;
        _isLink = isLink;
        _sizeUnknown = sizeUnknown;
        _created = created;
        _modified = modified;
        _visibility = visibility;
        _childStart = childStart;
        _children = children;
        _placed = placed;
        _listingRefused = listingRefused;
        Scanned = this;
    }

    /// <summary>
    /// <paramref name="scanned"/> with <paramref name="removed"/> taken out. Every array is the
    /// scanned tree's own, so this costs what was taken out and not the size of the tree.
    /// </summary>
    private ExploreTree(ExploreTree scanned, RemovedSinceScan removed)
    {
        RootPath = scanned.RootPath;
        RootNode = scanned.RootNode;
        ChildOrder = scanned.ChildOrder;
        _names = scanned._names;
        _parents = scanned._parents;
        _sizes = scanned._sizes;
        _lengths = scanned._lengths;
        _storage = scanned._storage;
        _isDirectory = scanned._isDirectory;
        _isLink = scanned._isLink;
        _sizeUnknown = scanned._sizeUnknown;
        _created = scanned._created;
        _modified = scanned._modified;
        _visibility = scanned._visibility;
        _childStart = scanned._childStart;
        _children = scanned._children;
        _placed = scanned._placed;
        _listingRefused = scanned._listingRefused;
        _removed = removed;
        Scanned = scanned;
    }

    /// <summary>
    /// The tree as the scan found it: this one, or for a tree with items taken out since
    /// (<see cref="Without"/>), the one they were taken from. Whatever was measured from the scan,
    /// such as what grew since the scan before it, describes that tree and holds for this one, whose
    /// node numbers are the same.
    /// </summary>
    public ExploreTree Scanned { get; }

    /// <summary>How many items have been taken out since the scan. See <see cref="Without"/>.</summary>
    public int RemovedSinceScan => _removed?.Count ?? 0;

    /// <summary>Where the scan started, as the user picked it — <c>C:\</c>.</summary>
    public string RootPath { get; }

    /// <summary>
    /// The node <see cref="RootPath"/> names. Not always zero: the master file table addresses its
    /// root by a fixed record number, and a scan scoped to a folder is rooted at whichever record
    /// holds that folder. Remapping every node to make it zero would cost a pass over the whole
    /// volume to save an assumption nothing needs.
    /// </summary>
    public int RootNode { get; }

    /// <summary>
    /// The order <see cref="ChildrenOf"/> returns siblings in.
    ///
    /// <para>Exposed because it is a precondition rather than a detail. Squarification is defined
    /// only over a decreasing sequence, so a consumer holding a tree ordered any other way has to
    /// draw something else — and a view inferring that from whether a scan was running would be
    /// inferring the one thing that decides whether its picture means anything.</para>
    /// </summary>
    public ExploreChildOrder ChildOrder { get; }

    /// <summary>
    /// Bytes on the disk under the root, as far as the scan established them. See
    /// <see cref="SizeOf"/> for why this is the figure rather than the length.
    /// </summary>
    public long TotalBytes => SizeOf(RootNode);

    /// <summary>The length of every file under the root added up: what Explorer calls their size.</summary>
    public long TotalLength => LengthOf(RootNode);

    /// <summary>
    /// Whether any node has a size the scan could not establish, which makes every total above it
    /// a lower bound rather than a measurement.
    ///
    /// <para>Reported rather than refused, and that is the opposite of what the deletion path does.
    /// <see cref="Scanning.Mft.MftVolumeIndex.TryMeasure"/> answers null rather than a total that is
    /// short, because a number that decides a deletion has to be right. Nothing here decides a
    /// deletion, so the trade goes the other way: a picture complete apart from a handful of
    /// records, saying so, beats no picture at all.</para>
    /// </summary>
    public bool HasUnknownSizes => _sizeUnknown[RootNode];

    public int NodeCount => _names.Length;

    public string NameOf(int node) => _names[node];

    /// <summary>
    /// Find each node in <paramref name="later"/> by its path, where that is a scan of the same place
    /// (see <see cref="ExploreCounterparts"/>), or by its number in a step of a replay that ends at this
    /// tree, which numbers every node of this one the same (<see cref="History.GrowthReplay"/>).
    /// </summary>
    public Func<int, int?>? CounterpartsIn(Layout.ISizedTree later) =>
        later is ExploreTree scanned && ExplorePlace.IsRootedAlike(this, scanned)
            ? new ExploreCounterparts(this, scanned).Find
            : later is History.GrowthReplayFrame frame && ReferenceEquals(frame.Replay.Today, this)
                ? static node => node
                : null;

    /// <summary>
    /// The bytes this node occupies on the disk, which is what every picture of the tree draws.
    ///
    /// <para><b>Space on disk rather than length</b>, because the question this page answers is where
    /// the drive's space went (§7.1). A file kept only in the cloud is its full length and holds
    /// nothing here, so drawn by length a cloud library is the largest block on a drive it occupies
    /// none of, and freeing it frees nothing. On the file table this is exact, cluster rounding
    /// included. The walk learns lengths alone, so there it is the length for every file nothing marks
    /// as cloud, compressed or sparse, and what the file system states for the rest.</para>
    /// </summary>
    public long SizeOf(int node) => _removed is null ? _sizes[node] : _sizes[node] - _removed.BytesLost(node);

    /// <summary>
    /// The length of this file, or of every file at or below this folder: what Explorer calls its
    /// size, and what a download of it would cost.
    /// </summary>
    public long LengthOf(int node) => _removed is null ? _lengths[node] : _lengths[node] - _removed.LengthLost(node);

    /// <summary>
    /// Why this file occupies less than its length, or every reason found at or below this folder.
    /// </summary>
    public FileStorage StorageOf(int node) => _storage[node];

    public bool IsDirectory(int node) => _isDirectory[node];

    /// <summary>
    /// What a layout descends into. A directory is one even when it is empty, which is what keeps an
    /// empty folder from reading as a file.
    /// </summary>
    bool Layout.ISizedTree.IsContainer(int node) => IsDirectory(node);

    /// <summary>
    /// Whether this node is a junction, a symbolic link or another name surrogate. Its target keeps
    /// its own place in the tree, so a link holds nothing here however much its path appears to.
    /// </summary>
    public bool IsLink(int node) => _isLink[node];

    /// <summary>Whether <see cref="SizeOf"/> for this node is a lower bound.</summary>
    public bool HasUnknownSizeBelow(int node) => _sizeUnknown[node];

    /// <summary>
    /// When this node was made, as the filesystem recorded it, or
    /// <see cref="ExploreTimestamp.Unknown"/> where the scan could not establish it.
    ///
    /// <para>The node's own date, never rolled up. A directory's creation time is the day it was
    /// made and stays that whatever happens inside it, which is exactly the fact worth having: it
    /// tells the age of an installation apart from the age of its contents.</para>
    /// </summary>
    public ExploreTimestamp CreatedOf(int node) => _created[node];

    /// <summary>
    /// The newest write at or below this node, or <see cref="ExploreTimestamp.Unknown"/> where
    /// nothing under it carried a date.
    ///
    /// <para><b>Rolled up, and that is the whole value of it.</b> A file answers for itself. A
    /// directory answers for everything inside it, because NTFS moves a directory's own timestamp
    /// when an entry is added, removed or renamed and leaves it alone when an entry's contents
    /// change — so the directory alone dates the last time its layout altered, not the last time
    /// anything in it was written. <see cref="Providers.DirectoryAge"/> reaches the same conclusion
    /// for a single location one level down; this is that rule applied to a whole volume, which is
    /// affordable here because the pass has already been made.</para>
    ///
    /// <para>This is what §8's first open question asked for. "Is this toolchain idle" is answered
    /// by the newest write anywhere beneath it and by nothing shallower — an Android SDK whose top
    /// directory was touched by an installer last week is idle if nothing inside it has been written
    /// in two years. Last-access times would answer it more directly and NTFS stops maintaining
    /// them by default, which is why the question was open.</para>
    /// </summary>
    public ExploreTimestamp ModifiedOf(int node) => _modified[node];

    /// <summary>
    /// Whether this node is hidden or a system file, as its own attributes say. Never rolled up: a
    /// visible file in a hidden folder is a visible file.
    /// </summary>
    public FileVisibility VisibilityOf(int node) => _visibility[node];

    /// <summary>
    /// Whether the walk was refused a listing of this folder, so what it holds is in the tree in part
    /// or not at all. Its size is unknown too (<see cref="HasUnknownSizeBelow"/>), but that says only
    /// that a total is short somewhere below; this names the folder, so a caller going through the
    /// files can say which one it could not read rather than read it as empty.
    ///
    /// <para>Never true on the file table's route, which lists nothing: a record it could not read
    /// might have been anywhere, so its root says the totals are lower bounds instead.</para>
    /// </summary>
    public bool ListingWasRefused(int node) => _listingRefused.Contains(node);

    public int ParentOf(int node) => _parents[node];

    /// <summary>
    /// This node's children, in <see cref="ChildOrder"/>. Fixed at build time because every
    /// consumer wants an order — a treemap needs descending size for its row packing, a list shows
    /// it directly, and a sunburst draws its arcs in it — so sorting once beats sorting per repaint
    /// (G4/G5).
    /// </summary>
    public ReadOnlySpan<int> ChildrenOf(int node) =>
        _removed is not null && _removed.TryChildren(node, out var kept)
            ? kept
            : _children.AsSpan(_childStart[node], _childStart[node + 1] - _childStart[node]);

    /// <summary>
    /// Whether this tree holds <paramref name="node"/>: a number in range, reachable from the root,
    /// and not taken out since the scan (<see cref="Without"/>). Everything the tree lists, draws or
    /// names a path for is a node it holds.
    /// </summary>
    public bool Holds(int node) =>
        node >= 0 && node < NodeCount && _placed[node] && _removed?.Covers(node, ParentOf) != true;

    /// <summary>
    /// This tree with <paramref name="removed"/> taken out, each with everything under it: the
    /// folders above each lose its size and length, it leaves its folder's children, and those are
    /// ordered again. The nodes keep their numbers, so a node of this tree is the same node of the
    /// one returned wherever that holds it (<see cref="Holds"/>). This tree itself, where none of
    /// <paramref name="removed"/> is in it.
    ///
    /// <para>What is answered for here is what the disk holds after a removal, without scanning it
    /// again. A removal takes a folder and everything in it, and nothing else, so the folders above
    /// it lose exactly its size. What only the scan measured stays as the scan found it: whether a
    /// size below a folder was unknown, the kinds of storage under it, and its newest write, each of
    /// which can only overstate what is left.</para>
    ///
    /// <para>The root stays, as the place the scan was of. Taken out, it leaves nothing under it.</para>
    /// </summary>
    public ExploreTree Without(IEnumerable<int> removed)
    {
        ArgumentNullException.ThrowIfNull(removed);

        return Exploring.RemovedSinceScan.Taking(this, _removed, removed) is { } taken
            ? new ExploreTree(Scanned, taken)
            : this;
    }

    /// <summary>
    /// The full path of <paramref name="node"/>, rebuilt by walking to the root.
    ///
    /// <para>Throws for a node this tree cannot place. Such nodes exist: the file-table route sizes
    /// its arrays to the whole record count and most of those slots are never filled, so their
    /// parent is a default zero and the walk would never terminate. Every node reachable through
    /// <see cref="ChildrenOf"/> from the root is safe by construction, and that is the only way
    /// anything gets one to pass here.</para>
    ///
    /// <para>Throwing rather than answering, because the caller wants this for a label today and to
    /// open a folder tomorrow. A guessed path is a wrong path, and the one thing worse than no
    /// answer is a plausible one. A caller that is asking whether the node has a path at all wants
    /// <see cref="TryPathOf"/> instead, which is the same walk without the throw.</para>
    /// </summary>
    public string PathOf(int node) =>
        TryPathOf(node) ?? throw new ArgumentOutOfRangeException(
            nameof(node), node, "This node is not reachable from the root, so it has no path.");

    /// <summary>
    /// The full path of <paramref name="node"/>, or null where this tree does not hold it
    /// (<see cref="Holds"/>): a number past its end, one whose parent chain does not reach the root,
    /// or one taken out since the scan.
    ///
    /// <para>Rebuilt rather than stored: a path per node would cost more than every other array
    /// here combined, and the UI asks for one only when the user points at something.</para>
    ///
    /// <para>Null rather than a throw is for the caller holding a node number from a <i>different</i>
    /// tree, where being unable to place it is the expected answer rather than a mistake — see
    /// <see cref="ExplorePlace"/>.</para>
    /// </summary>
    public string? TryPathOf(int node)
    {
        // Every way of "this tree cannot place it", asked before the walk rather than discovered
        // during it. A node number from another tree can be past the end of this one, and a record
        // the file table never described is in range and holds nothing — see Placed for why walking
        // out of one of those does not end where it looks as though it should.
        if (!Holds(node))
        {
            return null;
        }

        if (node == RootNode)
        {
            return RootPath;
        }

        var components = new List<string>();

        // Unbounded, and it terminates because the node is placed: every ancestor of a placed node
        // is placed, and the chain of them ends at the root by the definition of reachability.
        for (var current = node; current != RootNode; current = _parents[current])
        {
            components.Add(_names[current]);
        }

        components.Reverse();

        return Path.Combine([RootPath, .. components]);
    }

    /// <summary>
    /// Assemble a tree from the arrays a reader produced: invert the parent links, total each
    /// subtree, and order every child list by size.
    ///
    /// <para>Shared by both routes on purpose. The master file table and the directory walk gather
    /// entirely differently, but what they gather is the same shape, and these three steps are
    /// where a mistake would be invisible in the picture rather than obvious in it.</para>
    ///
    /// <paramref name="rootNode"/> is where the scan started, which for the file table is a record
    /// number rather than a position: record 5 for a whole volume, and the record holding the folder
    /// for a scan scoped to one. Nodes outside that subtree stay in the arrays and are simply never
    /// reached, so nothing here has to know which case it is in.
    ///
    /// <paramref name="present"/> says which slots the reader actually filled. The walk fills every
    /// one; the table leaves free and unreadable records empty, and without this those read as a
    /// forest of node-zero children that would attach the whole volume to the root a second time.
    ///
    /// <paramref name="childOrder"/> settles the last of the three. See
    /// <see cref="ExploreChildOrder"/> for why a tree still being filled in wants a different one
    /// from a finished tree.
    ///
    /// <paramref name="listingRefused"/> names the folders the walk was refused a listing of, which
    /// <see cref="ListingWasRefused"/> answers for. A set rather than a column, because a refusal is
    /// rare against the millions of nodes a column would spend a slot on each.
    ///
    /// <paramref name="nameRanks"/> is each node's place by name among its siblings, which the walk
    /// knows as soon as a folder's children arrive, and a tree ordered
    /// <see cref="ExploreChildOrder.ByName"/> needs it. Each child is placed in name order as it is
    /// linked, so that tree is not sorted at all — the sort that, done again on every snapshot of a
    /// scan in progress, was most of its cost.
    ///
    /// <paramref name="ct"/> is asked between the passes, so a snapshot of a walk that has ended
    /// stops within one pass rather than holding the end of the walk until it is finished.
    /// </summary>
    internal static ExploreTree Create(
        string rootPath,
        int rootNode,
        string[] names,
        int[] parents,
        long[] sizes,
        long[] lengths,
        FileStorage[] storage,
        bool[] isDirectory,
        bool[] isLink,
        bool[] sizeUnknown,
        ExploreTimestamp[] created,
        ExploreTimestamp[] modified,
        FileVisibility[] visibility,
        bool[] present,
        ExploreChildOrder childOrder,
        IReadOnlyCollection<int>? listingRefused = null,
        int[]? nameRanks = null,
        CancellationToken ct = default)
    {
        if (childOrder is ExploreChildOrder.ByName && nameRanks is null)
        {
            throw new ArgumentNullException(nameof(nameRanks), "A tree in name order is placed by its name ranks.");
        }

        var (childStart, children) = InvertParentLinks(parents, present, nameRanks);
        ct.ThrowIfCancellationRequested();

        var order = TopDownOrder(childStart, children, rootNode);
        ct.ThrowIfCancellationRequested();

        RollUp(order, parents, sizes, lengths, storage, sizeUnknown, modified, rootNode);
        ct.ThrowIfCancellationRequested();

        if (childOrder is ExploreChildOrder.BySize)
        {
            SortChildren(childStart, children, sizes, names);
        }

        return new ExploreTree(
            rootPath, rootNode, names, parents, sizes, lengths, storage, isDirectory, isLink, sizeUnknown,
            created, modified, visibility, childStart, children, Placed(order, names.Length), childOrder,
            listingRefused is { Count: > 0 } ? listingRefused.ToFrozenSet() : FrozenSet<int>.Empty);
    }

    /// <summary>
    /// Children in compressed-row form, by counting sort: one pass to count children per node, a
    /// prefix sum, then one pass to place them. Two linear passes and two arrays, with no list per
    /// directory — which on a volume with 240k directories is 240k objects avoided for a structure
    /// that never changes after construction (G4).
    ///
    /// <para>With <paramref name="nameRanks"/>, a child goes where its rank puts it in its parent's
    /// range rather than after the sibling placed before it, so the ranges come out in name order.
    /// Without, each parent's own start is the cursor that places its children, and the starts are
    /// moved back afterwards: one pass rather than a cursor array the size of the tree.</para>
    /// </summary>
    private static (int[] Start, int[] Children) InvertParentLinks(int[] parents, bool[] present, int[]? nameRanks)
    {
        var count = parents.Length;
        var start = new int[count + 1];

        for (var i = 0; i < count; i++)
        {
            // A node that is its own parent is not linked to anything, because linking it would
            // make the tree cyclic and the walk below would never terminate. Every reader marks its
            // scan root that way, so this is how the root stays out of its own child list.
            //
            // Stated as the shape rather than as "is it the root", because a scan rooted at a
            // folder leaves the volume's own root somewhere above it — still present, still its own
            // parent, and still unreachable from the scan's root. Testing the node number alone
            // would link that one under itself.
            if (present[i] && parents[i] != i)
            {
                start[parents[i] + 1]++;
            }
        }

        for (var i = 0; i < count; i++)
        {
            start[i + 1] += start[i];
        }

        var children = new int[start[count]];

        if (nameRanks is not null)
        {
            for (var i = 0; i < count; i++)
            {
                if (present[i] && parents[i] != i)
                {
                    children[start[parents[i]] + nameRanks[i]] = i;
                }
            }

            return (start, children);
        }

        for (var i = 0; i < count; i++)
        {
            if (present[i] && parents[i] != i)
            {
                children[start[parents[i]]++] = i;
            }
        }

        // Each start has advanced to the end of its range, which is where the next node's range
        // starts, so every start now sits one place early. The last never moved, because no node
        // has a parent past the end.
        for (var node = count; node > 0; node--)
        {
            start[node] = start[node - 1];
        }

        start[0] = 0;

        return (start, children);
    }

    /// <summary>
    /// Every node reachable from the root, parents before children.
    ///
    /// <para>Iterative, because recursion would overflow the stack on a deep <c>node_modules</c>
    /// tree — exactly the kind of tree this feature exists to show. Reachability is not incidental
    /// either: a table read from a live volume holds records whose parent chain does not reach the
    /// root, and totalling those into anything would attribute bytes to a directory that does not
    /// contain them.</para>
    ///
    /// <para>Breadth first, so that the order is its own queue: one array sized to the tree, with no
    /// stack beside it and no list doubling its way through the large object heap. Each node has one
    /// parent, so none is reached twice and the array cannot overflow. Nothing needs depth first:
    /// <see cref="RollUp"/> and <see cref="Placed"/> ask only that a parent comes before its
    /// children.</para>
    /// </summary>
    private static ReadOnlySpan<int> TopDownOrder(int[] childStart, int[] children, int rootNode)
    {
        var order = new int[childStart.Length - 1];
        order[0] = rootNode;
        var reached = 1;

        for (var next = 0; next < reached; next++)
        {
            var node = order[next];

            for (var i = childStart[node]; i < childStart[node + 1]; i++)
            {
                order[reached++] = children[i];
            }
        }

        return order.AsSpan(0, reached);
    }

    /// <summary>
    /// Which nodes the root can actually reach, taken from the traversal that has just established
    /// it. A byte per node, so that asking is a lookup rather than a walk.
    ///
    /// <para><b>Reachability is not the same question as whether the reader filled the slot, and
    /// the file table is where they come apart.</b> An undescribed record keeps the array default
    /// for its parent, which is record zero — and on a real volume record zero is <c>$MFT</c>,
    /// described, and a child of the volume's root. So a walk up from an undescribed record does
    /// arrive at the root, through a node that has nothing to do with it, and produces a path that
    /// looks entirely ordinary. That is the plausible wrong answer <see cref="PathOf"/> exists to
    /// refuse, and no walk can detect it from the parent links alone.</para>
    /// </summary>
    private static bool[] Placed(ReadOnlySpan<int> order, int count)
    {
        var placed = new bool[count];

        foreach (var node in order)
        {
            placed[node] = true;
        }

        return placed;
    }

    /// <summary>
    /// Total each subtree by walking the top-down order backwards, so every node's children are
    /// finished before it is reached. One pass, no recursion and no second traversal.
    ///
    /// <para>An unknown size travels the same way. One record the scan could not size makes every
    /// total above it a lower bound, and a lower bound presented as a measurement is the one thing
    /// a size picture must not do.</para>
    ///
    /// <para>So does the last-written time, and it rides this pass rather than getting one of its
    /// own precisely because the traversal is the cost. Each is combined by its own operation — sizes
    /// and lengths add, unknowns and storage spread, times take the later — and the same single
    /// ordering serves them all. See <see cref="ModifiedOf"/> for why a directory has to answer for its
    /// contents rather than for itself, and note that a creation time is deliberately absent from
    /// this: it is the one of the three that means something on its own.</para>
    /// </summary>
    private static void RollUp(
        ReadOnlySpan<int> order,
        int[] parents,
        long[] sizes,
        long[] lengths,
        FileStorage[] storage,
        bool[] sizeUnknown,
        ExploreTimestamp[] modified,
        int rootNode)
    {
        for (var i = order.Length - 1; i >= 0; i--)
        {
            var node = order[i];
            if (node == rootNode)
            {
                continue;
            }

            var parent = parents[node];
            sizes[parent] += sizes[node];
            lengths[parent] += lengths[node];
            storage[parent] |= storage[node];
            sizeUnknown[parent] |= sizeUnknown[node];

            // Unknown is zero, so it loses to every real date without a case of its own — which is
            // the answer that is wanted. A directory holding one file it could not date and one it
            // could is dated by the one it could, not reduced to "unknown" by the other.
            modified[parent] = ExploreTimestamp.Newer(modified[parent], modified[node]);
        }
    }

    /// <summary>
    /// Order each node's children by size, in place, once. See <see cref="ChildrenOf"/> for why the
    /// order belongs here rather than in each consumer. Name order needs no sort: see
    /// <see cref="Create"/>.
    /// </summary>
    private static void SortChildren(int[] childStart, int[] children, long[] sizes, string[] names)
    {
        // One delegate each for the whole tree rather than one per directory. A volume has hundreds
        // of thousands of directories (G5).
        var byName = ByName(names);
        Func<int, long> sizeOf = node => sizes[node];

        // One key buffer for every directory, sized to the largest, rather than one per directory.
        var most = 0;

        for (var node = 0; node + 1 < childStart.Length; node++)
        {
            most = Math.Max(most, childStart[node + 1] - childStart[node]);
        }

        var keys = new long[most];

        for (var node = 0; node + 1 < childStart.Length; node++)
        {
            var from = childStart[node];
            var length = childStart[node + 1] - from;

            if (length <= 1)
            {
                continue;
            }

            OrderBySize(children.AsSpan(from, length), keys, sizeOf, byName);
        }
    }

    /// <summary>
    /// Put <paramref name="siblings"/> in <see cref="ExploreChildOrder.BySize"/>, in place: the largest
    /// first, and siblings of one size in name order. The one statement of that order, for a tree as
    /// it is scanned and for the child lists a removal changes (<see cref="Without"/>).
    /// </summary>
    /// <param name="keyBuffer">
    /// Room for at least one key per sibling, overwritten. Passed in so that a caller ordering many
    /// lists allocates it once.
    /// </param>
    internal static void OrderBySize(Span<int> siblings, Span<long> keyBuffer, Func<int, long> sizeOf, Comparison<int> byName)
    {
        var length = siblings.Length;
        var keys = keyBuffer[..length];

        for (var i = 0; i < length; i++)
        {
            // Negated, so the sort puts the largest first; siblings of one size are put in name
            // order below.
            keys[i] = -sizeOf(siblings[i]);
        }

        MemoryExtensions.Sort(keys, siblings);

        // Equal sizes are put in name order. Left as the sort leaves them, they can come out in a
        // different order in each tree of the same disk, and a map moving from one layout to the next
        // would show equal siblings trading places for no reason (#309).
        for (int start = 0, end; start < length; start = end)
        {
            for (end = start + 1; end < length && keys[end] == keys[start]; end++)
            {
            }

            if (end - start > 1)
            {
                MemoryExtensions.Sort(siblings[start..end], byName);
            }
        }
    }

    /// <summary>
    /// Compare two siblings by name, ordinally and without regard to case, and break a tie by node
    /// number.
    ///
    /// <para>Ordinal so that the same disk draws the same picture on every machine. Without regard
    /// to case because the alternative sorts every capitalised name ahead of every lower-case one,
    /// which reads as no order at all to somebody looking at the list.</para>
    ///
    /// <para>The tie-break is what makes this a total order rather than one whose equal cases fall
    /// to whatever the sort does with them. Two siblings can compare equal — a directory with case
    /// sensitivity enabled holds <c>Cache</c> and <c>cache</c> at once — and the size path three
    /// lines below says why an unspecified order among equals is not good enough here.</para>
    /// </summary>
    private static Comparison<int> ByName(string[] names) => (left, right) => CompareByName(names, left, right);

    /// <summary>Two siblings of this tree in name order. See <see cref="ByName"/>.</summary>
    internal int CompareByName(int left, int right) => CompareByName(_names, left, right);

    private static int CompareByName(string[] names, int left, int right) =>
        CompareByName(names[left], left, names[right], right);

    /// <summary>
    /// Two siblings in name order, by name and node number, for a caller that has the names before
    /// there is a tree to hold them. See <see cref="ByName"/>.
    /// </summary>
    internal static int CompareByName(string leftName, int left, string rightName, int right)
    {
        var byName = string.Compare(leftName, rightName, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : left.CompareTo(right);
    }
}
