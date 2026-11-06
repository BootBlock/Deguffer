using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Totals one node of a volume's table, and everything below it, under every guard a question asks
/// about at once.
///
/// <para>Apart from <see cref="MftVolumeIndex"/> because it answers a different question: what a
/// removal of a node it has already found would take, where the index finds the node a path names.
/// The traversal, the rules and the stores are the same under every guard, which changes only which
/// entries its removal keeps, so one pass serves every reading as the walk's one listing does.</para>
/// </summary>
internal sealed class MftSubtreeTally(MftVolumeTree tree, MftChildLinks links)
{
    /// <summary>The most guards one pass tallies, one bit each of a folder's mask.</summary>
    private const int MostGuards = sizeof(uint) * 8;

    /// <summary>
    /// One reading of <paramref name="node"/> for each of <paramref name="keeps"/>, or null where the
    /// table cannot answer for every one of them. The readings share every rule but the guard, so a
    /// size the table does not know declines them all.
    /// </summary>
    /// <param name="keepRoot">See <see cref="ScanBounds.KeepRoot"/>.</param>
    /// <param name="leftOut">See <see cref="ScanBounds.LeftOut"/>.</param>
    public MftMeasurement? TryTally(uint node, IReadOnlyList<MinimumAge> keeps, bool keepRoot, IReadOnlySet<uint> leftOut)
    {
        ArgumentOutOfRangeException.ThrowIfZero(keeps.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keeps.Count, MostGuards);

        if (tree.IsDirectory(node))
        {
            return TrySumSubtree(node, keeps, keepRoot, leftOut);
        }

        if (tree.SizeUnknown(node))
        {
            return null;
        }

        var newest = tree.NewestOf(node);
        var taken = new ScanSize(tree.AllocatedOf(node), tree.LogicalOf(node), Entries: 1);

        return new MftMeasurement(
            [.. keeps.Select(keep => keep.Protects(newest) ? new MftReading(ScanSize.Zero, WithheldRecent: true) : new MftReading(taken, false))],
            []);
    }

    /// <summary>
    /// Iterative depth-first sum, abandoned as soon as one entry's size turns out to be unknown.
    ///
    /// Recursion would be the obvious shape and would overflow the stack on a deep node_modules
    /// tree, which is exactly the kind of tree this tool exists to measure.
    ///
    /// Abandoning the whole subtree for one entry is the same trade <see cref="MftVolumeIndexBuilder"/>
    /// makes for a table it could not fully read: a total missing one file is still a total, and
    /// nothing downstream can tell it from a correct one. The cost is that this path takes the walk;
    /// the alternative cost is a cache reported as clear when it is not.
    ///
    /// <para><b>Entries are counted on the removal's terms, for each guard apart.</b> A folder stays
    /// while anything inside it stays, so a file a guard keeps has to mark every folder above it for
    /// that guard alone: a folder one guard keeps standing is still taken by the removal under
    /// another. Each folder reached records the position of the folder holding it and a mask of the
    /// guards it stands under, and a kept entry walks that chain.</para>
    ///
    /// <para><b>An Outlook mail store is left out on the same terms under every guard, and named
    /// (§9).</b> It keeps its folders standing exactly as a kept file does, and its path is rebuilt
    /// once, so the plan can protect it. A store whose path cannot be rebuilt takes the whole subtree
    /// to the walk rather than being left out unnamed, because a store nobody can name is one nothing
    /// can prove survived.</para>
    ///
    /// <para><b>A file is counted once for each of its names in the subtree</b>, because each is a
    /// node (see <see cref="MftLink"/>). That is what the walk lists and what the removal takes, and
    /// a name outside the subtree is outside the total.</para>
    /// </summary>
    private MftMeasurement? TrySumSubtree(uint root, IReadOnlyList<MinimumAge> keeps, bool keepRoot, IReadOnlySet<uint> leftOut)
    {
        var guards = keeps.Count;
        var everyGuard = guards == MostGuards ? uint.MaxValue : (1u << guards) - 1;

        var allocated = new long[guards];
        var logical = new long[guards];
        var entries = new long[guards];
        var withheld = 0u;

        var folders = new List<(int Holder, uint StandsUnder)>();
        var stores = new List<IReadOnlyList<string>>();

        var stack = new Stack<(uint Node, int Holder)>();
        stack.Push((root, -1));

        while (stack.TryPop(out var item))
        {
            var node = item.Node;

            // First, as the removal asks whether an entry is spared first: neither counted nor
            // entered, and every folder above it stays.
            if (leftOut.Contains(node))
            {
                Stay(item.Holder, everyGuard);
                continue;
            }

            // Next, as the removal asks it: the store stays whatever its age and
            // whatever reparse point it carries. The removal does not read which kind of mark a file
            // has, so a store the table calls a link is still left, and is named here to agree.
            if (tree.IsMailStore(node))
            {
                if (TryBuildPath(node) is not { } components)
                {
                    return null;
                }

                stores.Add(components);
                Stay(item.Holder, everyGuard);
                continue;
            }

            // A link holds nothing and contributes no bytes: whatever it points at keeps its own
            // place in the table, and the walk does not enter one either. A link to a folder is still
            // an entry, removed as a link, and the walk counts it by its own timestamp; a link to a
            // file is one the walk never sees, so it is not counted here either.
            if (tree.IsReparsePoint(node))
            {
                if (tree.IsDirectory(node))
                {
                    CountOrKeep(node, item.Holder, allocatedBytes: 0, logicalBytes: 0);
                }

                continue;
            }

            if (tree.SizeUnknown(node))
            {
                return null;
            }

            // The guard never prunes the traversal, and a folder is never kept by its own timestamp:
            // NTFS moves a directory's timestamp whenever an entry is added, removed or renamed, so a
            // `continue` here would drop a whole subtree of stale files because one recent thing was
            // written beside them. The guard is about files, and a folder stays only while something
            // inside it does.
            if (tree.IsDirectory(node))
            {
                // A folder emptied in place is never among what its removal takes, so it starts out
                // standing under every guard, and nothing above it needs marking.
                var position = folders.Count;
                folders.Add((item.Holder, keepRoot && item.Holder < 0 ? everyGuard : 0u));

                for (var i = links.Start[node]; i < links.Start[node + 1]; i++)
                {
                    stack.Push((links.Children[i], position));
                }

                continue;
            }

            CountOrKeep(node, item.Holder, tree.AllocatedOf(node), tree.LogicalOf(node));
        }

        foreach (var (_, standsUnder) in folders)
        {
            for (var guard = 0; guard < guards; guard++)
            {
                if ((standsUnder & (1u << guard)) == 0)
                {
                    entries[guard]++;
                }
            }
        }

        var readings = new MftReading[guards];

        for (var guard = 0; guard < guards; guard++)
        {
            readings[guard] = new MftReading(
                new ScanSize(allocated[guard], logical[guard], Entries: entries[guard]),
                (withheld & (1u << guard)) != 0);
        }

        return new MftMeasurement(readings, stores);

        // Count the entry into each guard's reading whose removal takes it. Each guard that keeps it
        // keeps every folder above it standing, for that guard alone.
        void CountOrKeep(uint node, int holder, long allocatedBytes, long logicalBytes)
        {
            var newest = tree.NewestOf(node);
            var keptBy = 0u;

            for (var guard = 0; guard < guards; guard++)
            {
                if (keeps[guard].Protects(newest))
                {
                    keptBy |= 1u << guard;
                    continue;
                }

                entries[guard]++;
                allocated[guard] += allocatedBytes;
                logical[guard] += logicalBytes;
            }

            if (keptBy != 0)
            {
                withheld |= keptBy;
                Stay(holder, keptBy);
            }
        }

        // Mark the folder holding an entry that stays, and every folder above it, under the guards
        // in the mask. A folder standing under a guard has every folder above it standing under that
        // guard already, so the climb carries only the guards not yet marked, and stops when none is
        // left.
        void Stay(int holder, uint under)
        {
            for (var position = holder; position >= 0; position = folders[position].Holder)
            {
                var (above, standsUnder) = folders[position];
                under &= ~standsUnder;

                if (under == 0)
                {
                    return;
                }

                folders[position] = (above, standsUnder | under);
            }
        }
    }

    /// <summary>
    /// Rebuild one node's path by walking <see cref="MftVolumeTree.ParentOf"/> up to the root. A
    /// link's path is its own: its name, under the directory holding that name.
    ///
    /// Returns null for a record whose chain does not reach the root — an orphan, or a parent
    /// pointing outside the table — and for one whose chain passes through a link. The first is a
    /// real condition on a live volume rather than a corruption check: a directory deleted while the
    /// table was being read leaves its children briefly unreachable. Either way a path that cannot
    /// be rebuilt must be dropped rather than guessed at, because a wrong path here is a wrong
    /// deletion target.
    /// </summary>
    private IReadOnlyList<string>? TryBuildPath(uint node)
    {
        // Deep enough for any real tree, and bounded so a cyclic parent chain terminates instead of
        // hanging the scan. NTFS itself cannot express a path with this many components.
        const int MaximumDepth = 512;

        var components = new List<string>();
        var current = node;

        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            if (current == MftRecord.RootRecordNumber)
            {
                components.Reverse();
                return components;
            }

            if (current >= tree.NodeCount)
            {
                return null;
            }

            // A link, or anything under one. The walk never enters a reparse point, so a path that
            // passes through one is not something the guaranteed route could ever have produced —
            // and the deletion target it names is a pointer, not the thing the user asked about.
            //
            // The name is a directory's, or a mail store's at the leaf: the only file whose path the
            // table is ever asked to rebuild. A store at the leaf is the one entry allowed to carry a
            // reparse point, because it is a file no path passes through, and the removal leaves it
            // whatever mark it carries.
            if ((tree.IsReparsePoint(current) && !(depth == 0 && tree.IsMailStore(current)))
                || (tree.DirectoryName(current) ?? tree.MailStoreName(current)) is not { } component)
            {
                return null;
            }

            components.Add(component);

            var parent = tree.ParentOf(current);

            // Only the root is its own parent; anything else claiming to be would loop forever.
            if (parent == current)
            {
                return null;
            }

            current = parent;
        }

        return null;
    }
}
