using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Exploring;

/// <summary>
/// Builds an <see cref="ExploreTree"/> straight from a volume's master file table — §5.5's fast
/// path, applied to the whole volume rather than to a handful of named locations.
///
/// <para>This is where the table pays for itself. The deletion path asks it about a dozen paths and
/// measured the index costing more to build than walking those paths cost outright
/// (<c>docs/todo/after-the-scanner.md</c>, item 7). Drawing the whole volume is the opposite trade:
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
    /// Read <paramref name="source"/> into a tree rooted at <paramref name="rootPath"/>.
    ///
    /// <para>Best effort by design. A record this cannot read is skipped and the tree says its
    /// totals are lower bounds; a region that cannot be read at all ends the pass and keeps what was
    /// gathered. On a real volume the records that decline are the ones whose size lives in an
    /// extension record the parser does not follow — measured at 400 of 400 sampled, and the
    /// unfinished work is <c>docs/todo/after-the-scanner.md</c> item 6.</para>
    /// </summary>
    public static ExploreTree Read(
        IMftSource source,
        string rootPath,
        Action<long>? onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var records = (int)Math.Min(source.RecordCount, int.MaxValue);
        var root = (int)MftRecord.RootRecordNumber;

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
                    // Both other outcomes leave a slot empty. A free record genuinely holds
                    // nothing; an unreadable one holds something this cannot name or place, and
                    // there is no parent to attribute it to — so the loss is declared once, on the
                    // root, rather than guessed at somewhere in the middle of the tree.
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
                    sawUnreadableRecord |= outcome == MftParseOutcome.Unreadable
                        && (number < MftRecord.FirstUnnamedReservedRecord
                            || number >= MftRecord.ReservedRecordCount);

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
                present[number] = true;

                return true;
            },
            ct);

        // The root is its own parent and carries the volume's own name rather than the "." NTFS
        // gives it. Forced rather than trusted: a table whose record 5 did not parse would otherwise
        // leave the root absent, and every directory on the volume unreachable with it.
        names[root] = rootPath;
        parents[root] = root;
        isDirectory[root] = true;
        present[root] = true;
        sizeUnknown[root] |= couldNotReadWholeTable || sawUnreadableRecord;

        return ExploreTree.Create(
            rootPath, root, names, parents, sizes, isDirectory, isLink, sizeUnknown, present);
    }
}
