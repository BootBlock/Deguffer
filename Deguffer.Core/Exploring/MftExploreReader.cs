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
    /// <param name="snapshotAssembly">
    /// How a snapshot is made, or null to take none, as for a caller that draws nothing until the
    /// read ends. See
    /// <see cref="ScanSnapshots"/> for when one is taken, and <see cref="Snapshot"/> for what it holds.
    /// </param>
    /// <param name="clock">What the snapshots' cadence is measured by. The system's where null.</param>
    /// <param name="timings">
    /// Told when each of the read's six stages ends: the four of
    /// <see cref="MftRecordStream.TryReadAll"/>, then <see cref="MftBuildStage.LinkPlacement"/> and
    /// <see cref="MftBuildStage.TreeAssembly"/>. Null for a read nobody measures, as a scan's is.
    /// </param>
    public static MftExploreRead Read(
        IMftSource source,
        string rootPath,
        IReadOnlyList<string> components,
        TableTuning tuning,
        IProgress<ExploreProgress>? progress,
        CancellationToken ct,
        SnapshotAssembly? snapshotAssembly = null,
        TimeProvider? clock = null,
        MftStageTimings? timings = null)
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
        var snapshots = progress is not null && snapshotAssembly is not null
            ? new ScanSnapshots(
                order => stopped => Snapshot(columns, names, rootPath, components, order, stopped),
                clock ?? TimeProvider.System,
                snapshotAssembly)
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

                    // The pool is shared by every parse thread and by a snapshot, and finds a name
                    // it already holds without a lock. See NamePool.
                    columns.NameNumbers[number] = names.Intern(name);

                    columns.Describe((int)number, record, (int)record.ParentRecordNumber);
                    return true;
                },
                done => progress?.Report(new ExploreProgress(
                    done,
                    total,
                    BytesSeen: 0,
                    // None from the report that the whole table has been dealt with: the finished
                    // tree is assembled next, and replaces it. None from the report at nought
                    // either, which has nothing to draw: a snapshot is a pass over every slot, and
                    // one started there would push the first that can draw something back by the
                    // snapshot interval at least, and on a large table by a multiple of that pass.
                    0 < done && done < total ? snapshots?.Take() : null)),
                ct,
                timings);
        }
        finally
        {
            // Before anything below writes to the columns. A snapshot copies them beside the parse
            // threads by the rule MftExploreColumns states, and nothing after this keeps that rule.
            snapshots?.Stop();
        }

        // In record order, then by folder and name, so one table always numbers its links alike
        // whatever order the parse threads met them in.
        var links = linksMet
            .OrderBy(met => met.Record)
            .ThenBy(met => met.Link.ParentRecordNumber)
            .ThenBy(met => met.Link.Name, StringComparer.Ordinal)
            .ToArray();

        // Columns of their own, sized to the links. The record columns are sized to every record on
        // the volume, and every system volume has links, so growing those to hold them would copy
        // each one whole on the large object heap, which ExploreTree.Create then copies again to keep
        // what the root reaches. Each link is numbered after every record all the same, and Create
        // compacts the two sets into one copy.
        MftExploreColumns? linkColumns = null;

        if (links.Length > 0)
        {
            linkColumns = new MftExploreColumns(links.Length);

            for (var i = 0; i < links.Length; i++)
            {
                var (_, described, link) = links[i];

                linkColumns.NameNumbers[i] = names.Intern(link.Name);
                linkColumns.Describe(i, described, (int)link.ParentRecordNumber);
            }
        }

        timings?.End(MftBuildStage.LinkPlacement);

        if (timings is not null)
        {
            timings.LinksPlaced = links.Length;
        }

        // A record the pass could not place might have been anywhere, this folder included, so the
        // caveat lands on whatever the scan is rooted at. Attributing it only to the volume's root
        // would let a scoped scan report a total as exact on the strength of a record it never read.
        //
        // By size, and there is no choice to make here: this is the whole table, and nothing will
        // grow it to rearrange.
        var rootName = names.Intern(rootPath);
        var (tree, reason) = MftExploreRooting.Assemble(
            columns,
            linkColumns,
            names.Freeze(),
            rootName,
            rootPath,
            components,
            new MftExploreRooting.AssembledAs(ExploreChildOrder.BySize, LowerBound: couldNotReadWholeTable || sawUnreadableRecord, MatchCaseOnly: false, Snapshot: false),
            ct);
        timings?.End(MftBuildStage.TreeAssembly);

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
    /// <para><b>Ordered as it was asked for</b>, as the walk's snapshots are: by name where the view
    /// allows it, so a folder that grows widens where it is rather than overtaking its siblings
    /// (<see cref="ScanSnapshots.Interval"/>). Nothing that decides a removal reads one: <see cref="ExploreGate"/> holds the page's actions while a scan
    /// runs, and the finished tree replaces the last snapshot.</para>
    /// </summary>
    private static ExploreTree? Snapshot(
        MftExploreColumns columns,
        NamePool names,
        string rootPath,
        IReadOnlyList<string> components,
        ExploreChildOrder order,
        CancellationToken stopped)
    {
        var copy = columns.CopyPresent(stopped);

        // Frozen after the copy, not before. A slot's name was numbered before its flags were
        // released, so every number the copy holds is in a pool frozen once the copy has ended.
        // The root's name is numbered first for the same reason.
        var rootName = names.Intern(rootPath);
        var frozen = names.Freeze();

        return MftExploreRooting.Assemble(
            copy,
            links: null,
            frozen,
            rootName,
            rootPath,
            components,
            new MftExploreRooting.AssembledAs(order, LowerBound: true, MatchCaseOnly: true, Snapshot: true),
            stopped).Tree;
    }
}
