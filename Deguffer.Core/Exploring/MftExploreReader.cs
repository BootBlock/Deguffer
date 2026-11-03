using System.Collections.Concurrent;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What reading the table produced.
/// </summary>
/// <param name="Tree">
/// The tree, or null where the table does not describe the location the scan was rooted at.
/// </param>
/// <param name="Reason">
/// Why the caller has to walk instead. Meaningful only where <paramref name="Tree"/> is null.
/// </param>
/// <param name="WholeTable">
/// Whether every record in use was read. Where it is false the tree is drawn from part of the table,
/// and its root says its totals are lower bounds.
/// </param>
/// <param name="EveryRecordRead">
/// Whether every record in use was read and placed: <paramref name="WholeTable"/>, and no record the
/// pass could not read. A record it could not read might have been anywhere on the volume, so where
/// this is false no folder in the tree can be taken to hold all it holds, however whole the table
/// read was.
/// </param>
internal readonly record struct MftExploreRead(ExploreTree? Tree, FallbackReason Reason, bool WholeTable, bool EveryRecordRead);

/// <summary>
/// Builds an <see cref="ExploreTree"/> straight from a volume's master file table — §5.5's fast
/// path, applied to a whole volume or to one folder on it rather than to a handful of named
/// locations.
///
/// <para>This is where the table pays for itself. The deletion path asks it about a dozen paths, and
/// measured, building the index there was sometimes slower than walking those paths outright, so it
/// races the walk (<c>docs/todo/after-the-scanner.md</c>, item 7). Drawing a whole drive is the
/// opposite trade:
/// one pass over the table answers for every directory on the disk at once, and the walk it
/// replaces is the one §5.5 measured at over ten minutes.</para>
///
/// <para>Deliberately not <see cref="MftVolumeIndex"/>, and the difference is the point. That index
/// keeps names for directories only and abandons a volume rather than report a total that is short,
/// because its numbers decide deletions. This one keeps every name and reports what it could not
/// establish, because its numbers draw a picture — and nothing about a picture may be allowed to
/// relax a rule that governs a deletion.</para>
/// </summary>
internal static class MftExploreReader
{
    /// <summary>
    /// Read <paramref name="source"/> into a tree rooted at <paramref name="rootPath"/>, which
    /// <paramref name="components"/> locates below the volume's own root — empty for the volume
    /// itself.
    ///
    /// <para>The whole table is read whichever is asked for, and there is no cheaper pass over the
    /// table that would do. It is addressed by record number, and a record names only its immediate
    /// parent, so the record holding a folder cannot be found without having read the records that
    /// lead to it — and which those are is not known until they have been read.</para>
    ///
    /// <para>So a folder small enough to walk quickly is answered more slowly this way than by
    /// walking it. That is the trade §5.5 states in the other direction and it is taken knowingly:
    /// the route is chosen before anything is known about the folder's size, the table's cost is
    /// bounded by the volume rather than by the folder, and the case it exists for is the folder
    /// large enough that walking it is the ten minutes §5.5 measured.</para>
    ///
    /// <para>Best effort by design. A record this cannot read is skipped and the tree says its
    /// totals are lower bounds; a region that cannot be read at all ends the pass and keeps what was
    /// gathered. A file whose size or name is in an extension record is followed there. Where that
    /// record was caught mid-change, a lost size is marked unknown and a lost name makes the file
    /// one this could not read.</para>
    /// </summary>
    /// <param name="progress">
    /// Told how many records have been dealt with, as <see cref="MftRecordStream.TryReadAll"/>
    /// describes, out of the table's record count, and now and then the tree so far.
    /// </param>
    /// <param name="assembleSnapshot">
    /// How a snapshot is assembled (<see cref="ScanSnapshots.InBackground"/> for a scan), or null to
    /// take none, as for a caller that draws nothing until the read ends. See
    /// <see cref="ScanSnapshots"/> for when one is taken, and <see cref="Snapshot"/> for what it holds.
    /// </param>
    /// <param name="clock">What the snapshots' cadence is measured by. The system's where null.</param>
    public static MftExploreRead Read(
        IMftSource source,
        string rootPath,
        IReadOnlyList<string> components,
        TableTuning tuning,
        IProgress<ExploreProgress>? progress,
        CancellationToken ct,
        Func<Action, Task>? assembleSnapshot = null,
        TimeProvider? clock = null)
    {
        var total = source.RecordCount;
        var records = (int)Math.Min(total, int.MaxValue);

        // Never smaller than the reserved block, whatever the source claims to hold. The root is at
        // a fixed record number and is written below whether or not it parsed, so a table reporting
        // fewer records than that would otherwise index past the end of every column here. Slots
        // past the real count are simply absent, and an absent slot contributes nothing.
        var count = Math.Max(records, (int)MftRecord.ReservedRecordCount);

        var columns = new MftExploreColumns(count);
        var names = new NamePool();
        var namesGate = new Lock();

        // Written from several parse threads at once, and only ever to true, so no write can undo
        // another. The stream has finished with every thread before it returns, which is when this
        // is read.
        var sawUnreadableRecord = false;

        // Each of a file's other names, met on whichever parse thread, with the record it names. Each
        // becomes a node of its own once the table is read, so the file is drawn under every folder
        // that names it, as the walk draws it. See MftLink.
        var linksMet = new ConcurrentQueue<(int Record, MftRecord Described, MftLink Link)>();

        // Nothing is taken on the reporting thread, which is a parse thread holding its slot: the
        // copy of the columns is the assembling's, away from the read.
        var snapshots = progress is not null && assembleSnapshot is not null
            ? new ScanSnapshots(
                () => stopped => Snapshot(columns, names, namesGate, rootPath, components, count, stopped),
                clock ?? TimeProvider.System,
                assembleSnapshot)
            : null;

        bool couldNotReadWholeTable;

        try
        {
            couldNotReadWholeTable = !MftRecordStream.TryReadAll(
                source,
                records,
                tuning,
                (number, outcome, in record, name) =>
                {
                    if (outcome != MftParseOutcome.Parsed)
                    {
                        // Every other outcome leaves a slot empty, and only one of them leaves
                        // nothing missing. A free record genuinely holds nothing. An unreadable one
                        // holds a real file this cannot place — and there is no parent to attribute
                        // the loss to, so it is declared once, on the scan's root, rather than
                        // guessed at somewhere in the middle of the tree. That includes a file whose
                        // names are in extension records caught mid-change, which the stream has
                        // already tried to follow.
                        //
                        // Except across records 12 to 15, which are not damage but the format. NTFS
                        // holds those four back for future metadata, marks them in use, and gives
                        // them neither a $FILE_NAME nor an $ATTRIBUTE_LIST — precisely the shape the
                        // parser has every reason to call unreadable, and precisely what it reports
                        // on every NTFS volume ever formatted. Without this carve-out the tree would
                        // say "some of this drive could not be read" about every drive, always, which
                        // is a caveat carrying no information at all.
                        //
                        // This is the same fact that took MftVolumeIndexBuilder's whole fast path
                        // out for six weeks. The bound is both-ended there for a reason and it is
                        // both-ended here for the same one: records 0 to 11 are the named metadata
                        // files, and an unreadable one of those is real damage.
                        if (outcome == MftParseOutcome.Unreadable
                            && (number < MftRecord.FirstUnnamedReservedRecord || number >= MftRecord.ReservedRecordCount))
                        {
                            sawUnreadableRecord = true;
                        }

                        return true;
                    }

                    // A parent outside the table is ordinary on a live volume: a directory removed
                    // mid-read, or a table that grew after its size was measured. Such a name cannot
                    // be reached from the root, so it draws nothing and dropping it costs nothing.
                    // The file's other names are judged one by one.
                    foreach (var link in record.OtherLinks ?? [])
                    {
                        if (link.ParentRecordNumber < (uint)count)
                        {
                            linksMet.Enqueue(((int)number, record, link));
                        }
                    }

                    if (record.ParentRecordNumber >= (uint)count)
                    {
                        return true;
                    }

                    // The pool is shared by every parse thread and by a snapshot. What the lock
                    // covers is a lookup and, for a name not met before, a copy, against a record
                    // parsed outside it.
                    lock (namesGate)
                    {
                        columns.NameNumbers[number] = names.Intern(name);
                    }

                    columns.Describe((int)number, record, (int)record.ParentRecordNumber);
                    return true;
                },
                done => progress?.Report(new ExploreProgress(
                    done,
                    total,
                    BytesSeen: 0,
                    // None from the report that the whole table has been dealt with: the finished
                    // tree is assembled next, and replaces it.
                    done < total ? snapshots?.Take() : null)),
                ct);
        }
        finally
        {
            // Before anything below writes to the columns. A snapshot copies them beside the parse
            // threads by the rule MftExploreColumns states, and nothing after this keeps that rule.
            snapshots?.Stop();
        }

        // In record order, then by folder and name, so one table always numbers its links alike
        // whatever order the parse threads met them in. Grown once, and only where a link was met.
        var links = linksMet
            .OrderBy(met => met.Record)
            .ThenBy(met => met.Link.ParentRecordNumber)
            .ThenBy(met => met.Link.Name, StringComparer.Ordinal)
            .ToArray();

        if (links.Length > 0)
        {
            columns.Grow(count + links.Length);

            for (var i = 0; i < links.Length; i++)
            {
                var (_, described, link) = links[i];

                columns.NameNumbers[count + i] = names.Intern(link.Name);
                columns.Describe(count + i, described, (int)link.ParentRecordNumber);
            }
        }

        // A record the pass could not place might have been anywhere, this folder included, so the
        // caveat lands on whatever the scan is rooted at. Attributing it only to the volume's root
        // would let a scoped scan report a total as exact on the strength of a record it never read.
        //
        // By size, and there is no choice to make here: this is the whole table, and nothing will
        // grow it to rearrange.
        var rootName = names.Intern(rootPath);
        var (tree, reason) = Assemble(
            columns,
            names.Freeze(),
            rootName,
            rootPath,
            components,
            count,
            new AssembledAs(ExploreChildOrder.BySize, LowerBound: couldNotReadWholeTable || sawUnreadableRecord, MatchCaseOnly: false),
            ct);

        return new MftExploreRead(tree, reason, !couldNotReadWholeTable, !couldNotReadWholeTable && !sawUnreadableRecord);
    }

    /// <summary>
    /// The tree as the read stands, while the parse threads go on: a copy of every slot described so
    /// far (<see cref="MftExploreColumns.CopyPresent"/>), assembled as the finished read is, or null
    /// where the read has not yet reached the folder the scan is rooted at.
    ///
    /// <para><b>Only what is reachable.</b> A record whose chain of parents has not all been read
    /// hangs from nothing yet and is left out, as the finished tree leaves out a record no folder
    /// reaches. So how much a snapshot holds depends on the order of the records on disk, not on how
    /// far the read has got: a folder and everything below it appear together once its last ancestor
    /// is read. Measured on one system volume upgraded in place, <c>Users</c>, <c>Windows</c> and both
    /// <c>Program Files</c> folders sat about 1.7 million records in, so their contents arrived late
    /// in the read and all at once.</para>
    ///
    /// <para><b>Every total a lower bound.</b> The root is marked
    /// <see cref="ExploreNodeFlags.SizeUnknown"/>, which every total above an unread record reads as
    /// "at least". A hard link's other names are placed only once the read ends, so a snapshot draws
    /// a file under the name its record was placed by and no other.</para>
    ///
    /// <para><b>Ordered by name</b>, as the walk's snapshots are, so a folder that grows widens where
    /// it is rather than overtaking its siblings (<see cref="ScanSnapshots.Interval"/>). Nothing that
    /// decides a removal reads one: <see cref="ExploreGate"/> holds the page's actions while a scan
    /// runs, and the finished tree replaces the last snapshot.</para>
    /// </summary>
    private static ExploreTree? Snapshot(
        MftExploreColumns columns,
        NamePool names,
        Lock namesGate,
        string rootPath,
        IReadOnlyList<string> components,
        int records,
        CancellationToken stopped)
    {
        var copy = columns.CopyPresent(stopped);

        // Frozen after the copy, not before. A slot's name was numbered before its flags were
        // released, so every number the copy holds is in a pool frozen once the copy has ended.
        int rootName;
        NamePool.Frozen frozen;

        lock (namesGate)
        {
            rootName = names.Intern(rootPath);
            frozen = names.Freeze();
        }

        return Assemble(
            copy,
            frozen,
            rootName,
            rootPath,
            components,
            records,
            new AssembledAs(ExploreChildOrder.ByName, LowerBound: true, MatchCaseOnly: true),
            stopped).Tree;
    }

    /// <summary>
    /// What separates a snapshot's tree from the finished read's in <see cref="Assemble"/>.
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
    private readonly record struct AssembledAs(ExploreChildOrder ChildOrder, bool LowerBound, bool MatchCaseOnly);

    /// <summary>
    /// Root a tree at the folder <paramref name="components"/> name, or say why the table cannot
    /// answer for it. Writes to <paramref name="columns"/>, which <see cref="ExploreTree.Create"/>
    /// then takes.
    /// </summary>
    /// <param name="records">How many slots are records, rather than a hard link's other names.</param>
    private static (ExploreTree? Tree, FallbackReason Reason) Assemble(
        MftExploreColumns columns,
        NamePool.Frozen names,
        int rootName,
        string rootPath,
        IReadOnlyList<string> components,
        int records,
        AssembledAs assembled,
        CancellationToken ct)
    {
        var volumeRoot = (int)MftRecord.RootRecordNumber;

        // The volume's root is forced present whether or not record 5 parsed. Resolution starts
        // here and every directory on the volume hangs off it, so a table whose root record declined
        // would otherwise leave the whole disk unreachable — a full drive drawn as empty, from one
        // record.
        columns.Parents[volumeRoot] = volumeRoot;
        columns.Flags[volumeRoot] |= ExploreNodeFlags.Directory | ExploreNodeFlags.Present;

        var resolved = Resolve(components, columns, names, records, assembled.MatchCaseOnly);
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
        // to invent.
        columns.NameNumbers[root] = rootName;
        columns.Parents[root] = root;

        if (assembled.LowerBound)
        {
            columns.Flags[root] |= ExploreNodeFlags.SizeUnknown;
        }

        var tree = ExploreTree.Create(
            rootPath, root, columns.NameNumbers, names, columns.Parents, columns.Sizes, columns.Lengths,
            columns.Storage, columns.Flags, columns.Created, columns.Modified, columns.Visibility,
            assembled.ChildOrder, ct: ct);

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
        MftExploreColumns columns,
        NamePool.Frozen names,
        int records,
        bool matchCaseOnly) =>
        Descend(
            components,
            (int)MftRecord.RootRecordNumber,
            node => (columns.Flags[node] & ExploreNodeFlags.Link) != 0,
            node => (columns.Flags[node] & ExploreNodeFlags.Directory) != 0,
            (directory, name) => FindChild(directory, name, columns, names, records, matchCaseOnly));

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
        int directory, string name, MftExploreColumns columns, NamePool.Frozen names, int records, bool matchCaseOnly)
    {
        var (nameNumbers, parents, flags) = (columns.NameNumbers, columns.Parents, columns.Flags);
        int? differingInCase = null;

        for (var i = 0; i < records; i++)
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
