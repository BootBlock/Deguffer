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
internal readonly record struct MftExploreRead(ExploreTree? Tree, FallbackReason Reason);

/// <summary>
/// Builds an <see cref="ExploreTree"/> straight from a volume's master file table — §5.5's fast
/// path, applied to a whole volume or to one folder on it rather than to a handful of named
/// locations.
///
/// <para>This is where the table pays for itself. The deletion path asks it about a dozen paths and
/// measured the index costing more to build than walking those paths cost outright
/// (<c>docs/todo/after-the-scanner.md</c>, item 7). Drawing a whole drive is the opposite trade:
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
    /// <summary>How often the record count is reported. A batch is 1024 records, so this is rarely.</summary>
    private const int ProgressInterval = 65536;

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
    /// gathered. On a real volume the records that decline are the ones whose size lives in an
    /// extension record the parser does not follow — measured at 400 of 400 sampled, and the
    /// unfinished work is <c>docs/todo/after-the-scanner.md</c> item 6.</para>
    /// </summary>
    public static MftExploreRead Read(
        IMftSource source,
        string rootPath,
        IReadOnlyList<string> components,
        Action<long>? onProgress,
        CancellationToken ct)
    {
        var records = (int)Math.Min(source.RecordCount, int.MaxValue);
        var volumeRoot = (int)MftRecord.RootRecordNumber;

        // Never smaller than the reserved block, whatever the source claims to hold. The root is at
        // a fixed record number and is written below whether or not it parsed, so a table reporting
        // fewer records than that would otherwise index past the end of every array here. Slots
        // past the real count are simply absent, and an absent slot contributes nothing.
        var count = Math.Max(records, (int)MftRecord.ReservedRecordCount);

        var names = new string[count];
        var parents = new int[count];
        var sizes = new long[count];
        var isDirectory = new bool[count];
        var isLink = new bool[count];
        var sizeUnknown = new bool[count];

        // Four bytes per node each, not eight. This route sizes every array to the whole record
        // count before it reads one — 2.4M on an ordinary system volume — so a pair of DateTime
        // arrays here would be 38 MB allocated up front and mostly into slots no record ever fills.
        // See ExploreTimestamp for what the minute of precision buys.
        var created = new ExploreTimestamp[count];
        var modified = new ExploreTimestamp[count];

        var present = new bool[count];

        Array.Fill(names, string.Empty);

        var sawUnreadableRecord = false;

        var couldNotReadWholeTable = !MftRecordStream.TryReadAll(
            source,
            records,
            (number, outcome, in record) =>
            {
                if ((number & (ProgressInterval - 1)) == 0)
                {
                    onProgress?.Invoke(number);
                }

                if (outcome != MftParseOutcome.Parsed)
                {
                    // Every other outcome leaves a slot empty, and only one of them leaves nothing
                    // missing. A free record genuinely holds nothing. An unreadable one, and one
                    // whose identity lives in an extension record, both hold a real file this
                    // cannot place — and there is no parent to attribute the loss to, so it is
                    // declared once, on the scan's root, rather than guessed at somewhere in the
                    // middle of the tree.
                    //
                    // The second of those is the common one rather than the exotic one: NTFS moves
                    // a file's $FILE_NAME into an extension record once it has enough hard links to
                    // overflow its own record, which a system volume is full of. Counting it as a
                    // free record is how a drive comes to be reported short with no caveat at all.
                    //
                    // Except across records 12 to 15, which are not damage but the format. NTFS
                    // holds those four back for future metadata, marks them in use, and gives them
                    // neither a $FILE_NAME nor an $ATTRIBUTE_LIST — precisely the shape the parser
                    // has every reason to call unreadable, and precisely what it reports on every
                    // NTFS volume ever formatted. Without this carve-out the tree would say "some
                    // of this drive could not be read" about every drive, always, which is a
                    // caveat carrying no information at all.
                    //
                    // This is the same fact that took MftVolumeIndexBuilder's whole fast path out
                    // for six weeks. The bound is both-ended there for a reason and it is
                    // both-ended here for the same one: records 0 to 11 are the named metadata
                    // files, and an unreadable one of those is real damage.
                    sawUnreadableRecord |= outcome == MftParseOutcome.IdentityElsewhere
                        || (outcome == MftParseOutcome.Unreadable
                            && (number < MftRecord.FirstUnnamedReservedRecord
                                || number >= MftRecord.ReservedRecordCount));

                    return true;
                }

                // A parent outside the table is ordinary on a live volume: a directory removed
                // mid-read, or a table that grew after its size was measured. Such a record cannot
                // be reached from the root, so it draws nothing and dropping it costs nothing.
                if (record.ParentRecordNumber >= (uint)count)
                {
                    return true;
                }

                names[number] = record.Name;
                parents[number] = (int)record.ParentRecordNumber;
                sizes[number] = record.Size?.Logical ?? 0;
                isDirectory[number] = record.IsDirectory;
                isLink[number] = record.IsReparsePoint;
                sizeUnknown[number] = record.Size is null;
                created[number] = ExploreTimestamp.FromFileTime(record.CreatedFileTime);
                modified[number] = ExploreTimestamp.FromFileTime(record.LastWrittenFileTime);
                present[number] = true;

                return true;
            },
            ct);

        // The volume's root is forced present whether or not record 5 parsed. Resolution starts
        // here and every directory on the volume hangs off it, so a table whose root record declined
        // would otherwise leave the whole disk unreachable — a full drive drawn as empty, from one
        // record.
        parents[volumeRoot] = volumeRoot;
        isDirectory[volumeRoot] = true;
        present[volumeRoot] = true;

        var resolved = Resolve(components, names, parents, isDirectory, isLink, present, count);
        if (resolved.Node is not { } root)
        {
            return new MftExploreRead(Tree: null, resolved.Reason);
        }

        // The scan's root carries the path the user chose rather than the name NTFS holds for it —
        // "." for a volume root, and a bare folder name below one — and it is its own parent, which
        // is what keeps ExploreTree's link inversion from drawing the tree above the scope back in.
        //
        // Its dates are deliberately not forced along with them. The record holding this folder
        // carries them like any other, and where it did not parse there is nothing to put here but
        // the unknown the array already holds — a date for a folder nothing could read is not one
        // to invent.
        names[root] = rootPath;
        parents[root] = root;

        // A record the pass could not place might have been anywhere, this folder included, so the
        // caveat lands on whatever the scan is rooted at. Attributing it only to the volume's root
        // would let a scoped scan report a total as exact on the strength of a record it never read.
        sizeUnknown[root] |= couldNotReadWholeTable || sawUnreadableRecord;

        // By size, and there is no choice to make here: this route inverts the parent links once,
        // after the whole table has been read, so it never publishes a partial tree that a growing
        // size could rearrange.
        return new MftExploreRead(
            ExploreTree.Create(
                rootPath, root, names, parents, sizes, isDirectory, isLink, sizeUnknown, created,
                modified, present, ExploreChildOrder.BySize),
            FallbackReason.None);
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
        string[] names,
        int[] parents,
        bool[] isDirectory,
        bool[] isLink,
        bool[] present,
        int count)
    {
        var current = (int)MftRecord.RootRecordNumber;

        foreach (var component in components)
        {
            if (isLink[current])
            {
                return (null, FallbackReason.None);
            }

            if (!isDirectory[current]
                || FindChild(current, component, names, parents, present, count) is not { } next)
            {
                return (null, FallbackReason.MasterFileTableIncomplete);
            }

            current = next;
        }

        if (isLink[current])
        {
            return (null, FallbackReason.None);
        }

        return isDirectory[current]
            ? (current, FallbackReason.None)
            : (null, FallbackReason.MasterFileTableIncomplete);
    }

    /// <summary>
    /// A pass over the whole table for one path component.
    ///
    /// <para>Deliberately not <see cref="MftVolumeIndex"/>'s shape, which walks one directory's
    /// child links. Those links are what <see cref="ExploreTree.Create"/> builds, and it cannot run
    /// until the root is known, so at this point the only thing to scan is the flat parent array. A
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
    /// which is the plausible wrong answer this whole class is written to avoid.</para>
    ///
    /// <para>A record naming itself as its parent is no directory's child, which is the rule
    /// <see cref="ExploreTree.Create"/> builds its child lists by. Resolving through one would pick
    /// a node the tree then declines to link.</para>
    /// </summary>
    private static int? FindChild(
        int directory, string name, string[] names, int[] parents, bool[] present, int count)
    {
        int? differingInCase = null;

        for (var i = 0; i < count; i++)
        {
            if (!present[i] || parents[i] != directory || i == directory)
            {
                continue;
            }

            if (names[i].Equals(name, StringComparison.Ordinal))
            {
                return i;
            }

            if (differingInCase is null && names[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                differingInCase = i;
            }
        }

        return differingInCase;
    }
}
