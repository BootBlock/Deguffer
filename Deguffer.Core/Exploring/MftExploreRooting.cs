using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Exploring;

/// <summary>
/// Where in a volume's file table the folder a scan names is, and the tree rooted there: from the
/// columns a read filled (<see cref="Assemble"/>), or in a tree already built from them
/// (<see cref="Locate"/>).
///
/// <para>Apart from <see cref="MftExploreReader"/>, which reads the table, because the read builds
/// a tree from these columns twice over: a snapshot while the parse threads still go, from copies of
/// the flags and parents (<see cref="MftSnapshotColumns"/>), and the finished tree once they have
/// stopped. Both are rooted by the same rules here.</para>
/// </summary>
internal static class MftExploreRooting
{
    /// <summary>
    /// What separates a snapshot's tree from the finished read's in <see cref="Assemble"/>: see
    /// <see cref="MftExploreReader"/>.
    /// </summary>
    /// <param name="ChildOrder">How each folder's children are ordered.</param>
    /// <param name="LowerBound">Whether the root says its totals are lower bounds.</param>
    /// <param name="MatchCaseOnly">
    /// Whether each folder on the way to the scan's root is found only by its exact name. A snapshot
    /// is, because the folder named exactly may not have been read yet when one differing only in
    /// case has: rooted there, the snapshot would draw one folder's content under the other's name,
    /// and a pick made in it would carry by path into the finished tree. A path typed in another
    /// case is drawn when the read ends instead.
    /// </param>
    internal readonly record struct AssembledAs(ExploreChildOrder ChildOrder, bool LowerBound, bool MatchCaseOnly);

    /// <summary>
    /// Root a tree at the folder <paramref name="components"/> name, or say why the table cannot
    /// answer for it. Writes to the flags and parents it roots by, which <see cref="ExploreTree.Create"/>
    /// then takes with <paramref name="columns"/> and <paramref name="links"/>.
    /// </summary>
    /// <param name="columns">One slot a record.</param>
    /// <param name="links">
    /// One slot for each further name of a hard-linked file, numbered after every record, or null
    /// where there are none. None is a folder, so the folder is found among the records alone.
    /// </param>
    /// <param name="snapshot">
    /// For a snapshot, taken while the parse threads still write <paramref name="columns"/>: the
    /// copies of their flags and parents it roots by, and what the tree reuses. Null for the finished
    /// read, which roots by the read's own and hands them over.
    /// </param>
    public static (ExploreTree? Tree, FallbackReason Reason) Assemble(
        MftExploreColumns columns,
        MftExploreColumns? links,
        MftSnapshotColumns? snapshot,
        NamePool.Frozen names,
        int rootName,
        string rootPath,
        IReadOnlyList<string> components,
        AssembledAs assembled,
        CancellationToken ct)
    {
        var volumeRoot = (int)MftRecord.RootRecordNumber;
        var (flags, parents) = snapshot is null ? (columns.Flags, columns.Parents) : (snapshot.Flags, snapshot.Parents);
        var volumeRootRead = (flags[volumeRoot] & ExploreNodeFlags.Present) != 0;

        // The volume's root is forced present whether or not record 5 parsed. Resolution starts
        // here and every directory on the volume hangs off it, so a table whose root record declined
        // would otherwise leave the whole disk unreachable — a full drive drawn as empty, from one
        // record.
        parents[volumeRoot] = volumeRoot;
        flags[volumeRoot] |= ExploreNodeFlags.Directory | ExploreNodeFlags.Present;

        var resolved = Resolve(components, columns.NameNumbers, flags, parents, names, assembled.MatchCaseOnly);
        if (resolved.Node is not { } root)
        {
            return (null, resolved.Reason);
        }

        // The scan's root carries the path the user chose rather than the name NTFS holds for it —
        // "." for a volume root, and a bare folder name below one — and it is its own parent, which
        // is what keeps ExploreTree's link inversion from drawing the tree above the scope back in.
        //
        // Its dates are deliberately not forced along with them. The record holding this folder
        // carries them like any other, and where it did not parse there is nothing to put here but
        // the unknown the column already holds — a date for a folder nothing could read is not one
        // to invent. Nor are its size and the rest where record 5 is forced present without having
        // been read: the slot holds nothing, or, in a snapshot, whatever a parse thread is writing.
        parents[root] = root;

        if (assembled.LowerBound)
        {
            flags[root] |= ExploreNodeFlags.SizeUnknown;
        }

        var tree = ExploreTree.Create(
            rootPath, root, columns.NameNumbers, names, parents, columns.Sizes, columns.Lengths,
            columns.Storage, flags, columns.Created, columns.Modified, columns.Visibility,
            assembled.ChildOrder,
            links: links,
            snapshot: snapshot is not null,
            drawnRoot: new DrawnRoot(rootName, Unread: root == volumeRoot && !volumeRootRead),
            scratch: snapshot?.Scratch,
            ct: ct);

        return (tree, FallbackReason.None);
    }

    /// <summary>
    /// Walk <paramref name="components"/> down from the volume's root to the record that holds the
    /// folder they name, or say why the table cannot answer for it.
    ///
    /// <para>Two failures, and they are not the same thing to the user. A path reached through a
    /// junction has no record whose subtree is its content — whatever the link stands for keeps its
    /// own place under its real parent — so the table could never root here however the process is
    /// running, and the walk, which the shell resolves the link for, is simply the right route.
    /// Anything else means the table read and did not describe this folder, which is a route that
    /// was lost rather than one that never existed.</para>
    /// </summary>
    private static (int? Node, FallbackReason Reason) Resolve(
        IReadOnlyList<string> components,
        int[] nameNumbers,
        ExploreNodeFlags[] flags,
        int[] parents,
        NamePool.Frozen names,
        bool matchCaseOnly) =>
        Descend(
            components,
            (int)MftRecord.RootRecordNumber,
            node => (flags[node] & ExploreNodeFlags.Link) != 0,
            node => (flags[node] & ExploreNodeFlags.Directory) != 0,
            (directory, name) => FindChild(directory, name, nameNumbers, flags, parents, names, matchCaseOnly));

    /// <summary>
    /// The node of <paramref name="tree"/>, a tree rooted at its volume's top, that holds the folder
    /// <paramref name="components"/> name, or why the table cannot answer for it: the two failures
    /// <see cref="Resolve"/> tells apart, for the same reasons.
    ///
    /// <para>For a caller that read the table once to answer for several folders, so the folders are
    /// found in the tree rather than in the arrays it was built from, which the tree has taken.</para>
    ///
    /// <para><b>Each name matched exactly, with no fallback to one that differs only in case.</b> The
    /// caller's folders are final paths, which carry the case the disk holds, so a name that matches
    /// only when case is ignored is another folder: in a case-sensitive directory, <c>photos</c> beside
    /// a <c>Photos</c> the table did not place. Taking it would read one folder's content under the
    /// other's name, and a search naming both would list that content twice. A folder found no other
    /// way is walked.</para>
    /// </summary>
    public static (int? Node, FallbackReason Reason) Locate(ExploreTree tree, IReadOnlyList<string> components) =>
        Descend(
            components,
            tree.RootNode,
            tree.IsLink,
            tree.IsDirectory,
            (directory, name) => ExactChild(tree, directory, name));

    private static int? ExactChild(ExploreTree tree, int directory, string name)
    {
        foreach (var child in tree.ChildrenOf(directory))
        {
            if (tree.NameSpanOf(child).SequenceEqual(name))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Follow <paramref name="components"/> down from <paramref name="start"/>, asking each step's
    /// child of <paramref name="childNamed"/>: the one descent <see cref="Resolve"/> and
    /// <see cref="Locate"/> share, which says how the table fails to answer as <see cref="Resolve"/>
    /// describes. A link anywhere on the way, the folder itself included, is a route that never
    /// existed; anything else missing is one that was lost.
    /// </summary>
    private static (int? Node, FallbackReason Reason) Descend(
        IReadOnlyList<string> components,
        int start,
        Func<int, bool> isLink,
        Func<int, bool> isDirectory,
        Func<int, string, int?> childNamed)
    {
        var current = start;

        foreach (var component in components)
        {
            if (isLink(current))
            {
                return (null, FallbackReason.None);
            }

            if (!isDirectory(current) || childNamed(current, component) is not { } next)
            {
                return (null, FallbackReason.MasterFileTableIncomplete);
            }

            current = next;
        }

        if (isLink(current))
        {
            return (null, FallbackReason.None);
        }

        return isDirectory(current)
            ? (current, FallbackReason.None)
            : (null, FallbackReason.MasterFileTableIncomplete);
    }

    /// <summary>
    /// A pass over the whole table for one path component.
    ///
    /// <para>Deliberately not <see cref="MftVolumeIndex"/>'s shape, which walks one directory's
    /// child links. Those links are what <see cref="ExploreTree.Create"/> builds, and it cannot run
    /// until the root is known, so at this point the only thing to scan is the flat parent column. A
    /// path has a handful of components, and the comparison is an <c>int</c> per record with a name
    /// compared only where the parent matches — a few million of those against a table read that
    /// has already cost millions of disk records (G4).</para>
    ///
    /// <para>An exact match wins over one that differs only in case, and the fallback is what makes
    /// a path typed in the wrong case still resolve. NTFS has held per-directory case sensitivity
    /// since Windows 10 1803, and WSL sets it on the trees it creates, so a directory really can
    /// hold <c>Cache</c> and <c>cache</c> at once — the same shape
    /// <see cref="ExploreTree.ChildrenOf"/>'s comparer breaks ties for. Taking the first
    /// case-insensitive match there would draw the wrong folder under the right folder's name,
    /// which is the plausible wrong answer this whole class is written to avoid.
    /// <paramref name="matchCaseOnly"/> turns the fallback off, for <see cref="AssembledAs.MatchCaseOnly"/>'s
    /// reason.</para>
    ///
    /// <para>A record naming itself as its parent is no directory's child, which is the rule
    /// <see cref="ExploreTree.Create"/> builds its child lists by. Resolving through one would pick
    /// a node the tree then declines to link.</para>
    /// </summary>
    private static int? FindChild(
        int directory,
        string name,
        int[] nameNumbers,
        ExploreNodeFlags[] flags,
        int[] parents,
        NamePool.Frozen names,
        bool matchCaseOnly)
    {
        int? differingInCase = null;

        for (var i = 0; i < parents.Length; i++)
        {
            if ((flags[i] & ExploreNodeFlags.Present) == 0 || parents[i] != directory || i == directory)
            {
                continue;
            }

            var held = names[nameNumbers[i]];

            if (held.SequenceEqual(name))
            {
                return i;
            }

            if (!matchCaseOnly && differingInCase is null && held.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                differingInCase = i;
            }
        }

        return differingInCase;
    }
}
