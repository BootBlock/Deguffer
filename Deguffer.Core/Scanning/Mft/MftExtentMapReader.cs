namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Reads <see cref="MftExtentMap"/> out of record 0 — the entry <c>$MFT</c> keeps about itself —
/// and out of the extension records record 0's <c>$ATTRIBUTE_LIST</c> names, where the table has
/// grown fragmented enough for its run list to outgrow one record.
///
/// <para>The answer is the whole map or nothing. A map that ends short of the table would index
/// part of the volume and report short sizes for everything outside it, a wrong number with
/// nothing to mark it, where refusing sends the volume to the walk.</para>
///
/// <para>Every extension record of <c>$MFT</c> is itself a record of <c>$MFT</c>, so each is found
/// through the extents already read. The pieces are followed in the order of the clusters they
/// describe, which is the order in which each one's position becomes known.</para>
///
/// <para>Where <c>$MFT</c>'s <c>$BITMAP</c> is kept is read on the same pass, by
/// <see cref="MftBitmapLocator"/>, from the same records. Unlike the map, it is an answer or none:
/// a bitmap that cannot be located costs a pass that reads every record, not the volume.</para>
/// </summary>
internal static class MftExtentMapReader
{
    /// <summary>
    /// Read the map from <paramref name="record0"/>, which the update sequence fixup modifies in
    /// place, reading any other record or cluster it needs through <paramref name="read"/>.
    /// </summary>
    /// <param name="bitmap">
    /// Where <c>$MFT</c>'s <c>$BITMAP</c> is, or null where it could not be located. Meaningful only
    /// where the map was read.
    /// </param>
    public static bool TryRead(
        Span<byte> record0, int bytesPerCluster, ClusterReader read, out MftExtentMap map, out MftBitmapPlacement? bitmap)
    {
        map = default!;
        bitmap = null;

        if (MftRecordHeader.Read(record0, out var header) != MftParseOutcome.Parsed)
        {
            return false;
        }

        var self = new MftSegmentReference(0, header.Sequence);
        var pieces = new List<Piece>();
        var bitmapPieces = new MftBitmapLocator(bytesPerCluster);
        var visited = new HashSet<long>();

        // The piece at cluster 0 has to be in record 0: until it is read, no other record of the
        // table can be found, the extension records that hold the rest included.
        if (!TryReadPieces(record0[..header.UsedLength], header.FirstAttributeOffset, self, bytesPerCluster, read, pieces, bitmapPieces, out var list)
            || pieces.FindIndex(static p => p.LowestVcn == 0) is not (>= 0 and var first))
        {
            return false;
        }

        var dataSize = pieces[first].DataSize;

        if (list is not null && !TryFollow(list, self, record0.Length, bytesPerCluster, dataSize, read, pieces, bitmapPieces, visited))
        {
            return false;
        }

        pieces.Sort(static (a, b) => a.LowestVcn.CompareTo(b.LowestVcn));
        var (runs, clusters, whole) = Join(pieces);

        // Rounded up, because the table's last record can end part of the way into a cluster.
        if (!whole || (dataSize - 1) / bytesPerCluster >= clusters)
        {
            return false;
        }

        map = new MftExtentMap(dataSize, runs);
        bitmap = bitmapPieces.Locate(map, self, record0.Length, read, visited, listed: list is not null);
        return true;
    }

    /// <summary>
    /// Read every extension record <paramref name="list"/> names for a piece of <c>$MFT</c>'s
    /// <c>$DATA</c>, adding its pieces to <paramref name="pieces"/>, and check that the pieces found
    /// are exactly the pieces listed. Each record read is added to <paramref name="visited"/>.
    /// </summary>
    private static bool TryFollow(
        IReadOnlyList<MftAttributeListEntry> list,
        MftSegmentReference self,
        int bytesPerRecord,
        int bytesPerCluster,
        long dataSize,
        ClusterReader read,
        List<Piece> pieces,
        MftBitmapLocator bitmapPieces,
        HashSet<long> visited)
    {
        var listed = new HashSet<(MftSegmentReference Segment, long LowestVcn)>();
        var extensions = new List<(long LowestVcn, MftSegmentReference Segment)>();

        foreach (var entry in list)
        {
            if (entry.Type == MftRecordParser.AttributeBitmap && !entry.IsNamed)
            {
                bitmapPieces.Expect(entry);
                continue;
            }

            if (entry.Type != MftRecordParser.AttributeData || entry.IsNamed)
            {
                continue;
            }

            if (!listed.Add((entry.Segment, entry.LowestVcn)))
            {
                return false;
            }

            if (entry.Segment.Record != self.Record)
            {
                extensions.Add((entry.LowestVcn, entry.Segment));
            }
        }

        extensions.Sort(static (a, b) => a.LowestVcn.CompareTo(b.LowestVcn));

        var record = new byte[bytesPerRecord];

        foreach (var (_, segment) in extensions)
        {
            // One record can hold several pieces, and is read once for all of them. Two entries
            // naming it by two sequence numbers cannot both be right: the record is checked against
            // the first, and the other entry's piece is then missing from the check below.
            if (!visited.Add(segment.Record))
            {
                continue;
            }

            pieces.Sort(static (a, b) => a.LowestVcn.CompareTo(b.LowestVcn));
            var known = new MftExtentMap(dataSize, Join(pieces).Runs);

            // Only the extents read so far: a record they do not reach cannot be found, and the map
            // cannot be completed without it.
            if (!TryReadExtension(known, segment, self, bytesPerCluster, read, record, out var header)
                || !TryReadPieces(
                    record.AsSpan(0, header.UsedLength), header.FirstAttributeOffset, segment, bytesPerCluster, read, pieces, bitmapPieces, out _))
            {
                return false;
            }
        }

        // A piece the list does not name is as much damage as a listed piece that is missing: either
        // way the list and the records disagree about where the table is. Pieces are matched by the
        // whole reference, so a list naming record 0 by a sequence number it no longer has, caught
        // mid-change, matches nothing.
        return pieces.Count == listed.Count
            && pieces.TrueForAll(p => listed.Contains((p.Segment, p.LowestVcn)));
    }

    /// <summary>
    /// Read <paramref name="segment"/>, an extension record of <c>$MFT</c>, through
    /// <paramref name="known"/> into <paramref name="record"/>. False unless it is an extension
    /// record of <paramref name="self"/> still carrying the sequence number the list names it by.
    /// </summary>
    internal static bool TryReadExtension(
        MftExtentMap known,
        MftSegmentReference segment,
        MftSegmentReference self,
        int bytesPerCluster,
        ClusterReader read,
        byte[] record,
        out MftRecordHeader header)
    {
        header = default;

        return known.TryReadRecord(segment.Record, bytesPerCluster, read, record)
            && MftRecordHeader.ReadExtension(record, out header) == MftParseOutcome.Parsed
            && header.BaseReference == self
            && header.Sequence == segment.Sequence;
    }

    /// <summary>
    /// Add the pieces of the unnamed <c>$DATA</c> a record of <c>$MFT</c> holds to
    /// <paramref name="pieces"/>, offer its unnamed <c>$BITMAP</c> to <paramref name="bitmapPieces"/>,
    /// and read the record's <c>$ATTRIBUTE_LIST</c> where it has one. False where the record is
    /// malformed, or holds a list that cannot be read.
    /// </summary>
    private static bool TryReadPieces(
        ReadOnlySpan<byte> used,
        int firstAttribute,
        MftSegmentReference segment,
        int bytesPerCluster,
        ClusterReader read,
        List<Piece> pieces,
        MftBitmapLocator bitmapPieces,
        out IReadOnlyList<MftAttributeListEntry>? list)
    {
        list = null;
        var attributes = new MftAttributeEnumerator(used, firstAttribute);

        while (attributes.MoveNext())
        {
            var attribute = attributes.Current;

            if (attributes.CurrentType == MftRecordParser.AttributeList)
            {
                // Only a base record has a list, and only one.
                if (segment.Record != 0 || list is not null)
                {
                    return false;
                }

                list = TryReadList(attribute, bytesPerCluster, read);

                if (list is null)
                {
                    return false;
                }

                continue;
            }

            if (attributes.CurrentType == MftRecordParser.AttributeBitmap && MftRecordParser.IsUnnamed(attribute))
            {
                bitmapPieces.Offer(attribute, segment);
                continue;
            }

            if (attributes.CurrentType != MftRecordParser.AttributeData || !MftRecordParser.IsUnnamed(attribute))
            {
                continue;
            }

            // $MFT's data is always non-resident: it is the largest thing on the volume, so a
            // resident one would mean this is not $MFT at all.
            if (!MftNonResidentHeader.TryRead(attribute, out var header)
                || !TryReadPiece(attribute, header, segment, bytesPerCluster, out var piece))
            {
                return false;
            }

            pieces.Add(piece);
        }

        return !attributes.IsMalformed;
    }

    private static IReadOnlyList<MftAttributeListEntry>? TryReadList(
        ReadOnlySpan<byte> attribute, int bytesPerCluster, ClusterReader read)
    {
        if (attribute[0x08] == 0)
        {
            return MftRecordParser.TryReadResidentValue(attribute, out var value)
                ? MftAttributeList.TryReadEntries(value)
                : null;
        }

        return MftAttributeList.TryReadPlacement(attribute, out var runs, out var length)
            ? MftExtensionReader.TryReadList(read, bytesPerCluster, runs, length)
            : null;
    }

    /// <summary>
    /// One piece of a non-resident attribute of <c>$MFT</c>, accepted only where its runs cover
    /// exactly the clusters its header says it describes, and every cluster they name has a byte
    /// offset.
    /// </summary>
    internal static bool TryReadPiece(
        ReadOnlySpan<byte> attribute,
        MftNonResidentHeader header,
        MftSegmentReference segment,
        int bytesPerCluster,
        out Piece piece)
    {
        piece = default;

        // Only the piece at cluster 0 states the table's size. A highest cluster of long.MaxValue
        // cannot be followed by another, and refusing it is what keeps the count below in range.
        if ((header.LowestVcn == 0 && header.DataSize <= 0)
            || header.HighestVcn < header.LowestVcn
            || header.HighestVcn == long.MaxValue)
        {
            return false;
        }

        var runs = header.ReadRuns(attribute);
        var span = header.HighestVcn - header.LowestVcn;
        var lastAddressableCluster = long.MaxValue / bytesPerCluster;
        long covered = 0;

        foreach (var run in runs)
        {
            // Each bound is a subtraction, so a corrupt count near long.MaxValue cannot wrap a sum
            // negative and pass. The second is what lets a reader multiply any cluster these runs
            // name by the cluster size without the offset wrapping and throwing out of the read.
            if (run.ClusterCount - 1 > span - covered
                || (!run.IsSparse && run.StartCluster > lastAddressableCluster - run.ClusterCount))
            {
                return false;
            }

            covered += run.ClusterCount;
        }

        if (covered - 1 != span)
        {
            return false;
        }

        piece = new Piece(segment, header.LowestVcn, header.HighestVcn, runs, header.DataSize, header.InitializedSize);
        return true;
    }

    /// <summary>
    /// The runs of <paramref name="pieces"/>, already in order, laid end to end for as long as each
    /// starts where the last ended, and how many clusters that covers. <c>Whole</c> says whether
    /// every piece joined: a gap or an overlap stops the join, and the runs before it are the part
    /// of the table whose position is known.
    /// </summary>
    internal static (IReadOnlyList<DataRun> Runs, long Clusters, bool Whole) Join(IReadOnlyList<Piece> pieces)
    {
        var runs = new List<DataRun>();
        long clusters = 0;

        foreach (var piece in pieces)
        {
            if (piece.LowestVcn != clusters)
            {
                return (runs, clusters, false);
            }

            runs.AddRange(piece.Runs);
            clusters = piece.HighestVcn + 1;
        }

        return (runs, clusters, true);
    }

    /// <param name="Segment">The record holding the piece.</param>
    /// <param name="DataSize">The attribute's size, stated by the piece at cluster 0 and zero in the rest.</param>
    /// <param name="InitializedSize">How much of it was written, stated as the size is.</param>
    internal readonly record struct Piece(
        MftSegmentReference Segment,
        long LowestVcn,
        long HighestVcn,
        IReadOnlyList<DataRun> Runs,
        long DataSize,
        long InitializedSize);
}
