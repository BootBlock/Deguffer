namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Gathers <c>$MFT</c>'s unnamed <c>$BITMAP</c> from the records <see cref="MftExtentMapReader"/>
/// reads, and from any other extension record record 0's list names for it, and says where it is.
///
/// <para>All or nothing, as the map is, but nothing is not a failure here. Without a bitmap a pass
/// reads every record, which is what every pass did before there was one. So whatever does not add
/// up drops the bitmap and leaves the map alone: a piece the list does not name, a listed piece no
/// record holds, a second value kept in a record, or pieces that do not join.</para>
/// </summary>
internal sealed class MftBitmapLocator(int bytesPerCluster)
{
    private readonly List<MftExtentMapReader.Piece> _pieces = [];
    private readonly HashSet<(MftSegmentReference Segment, long LowestVcn)> _listed = [];
    private (MftSegmentReference Segment, byte[] Value)? _resident;
    private bool _broken;

    /// <summary>Take an unnamed <c>$BITMAP</c> found in <paramref name="segment"/>.</summary>
    public void Offer(ReadOnlySpan<byte> attribute, MftSegmentReference segment)
    {
        if (attribute[0x08] == 0)
        {
            if (_resident is not null || !MftRecordParser.TryReadResidentValue(attribute, out var value))
            {
                _broken = true;
                return;
            }

            _resident = (segment, value.ToArray());
            return;
        }

        if (!MftNonResidentHeader.TryRead(attribute, out var header)
            || !MftExtentMapReader.TryReadPiece(attribute, header, segment, bytesPerCluster, out var piece))
        {
            _broken = true;
            return;
        }

        _pieces.Add(piece);
    }

    /// <summary>Take an entry of record 0's list naming a piece of the unnamed <c>$BITMAP</c>.</summary>
    public void Expect(MftAttributeListEntry entry) =>
        _broken |= !_listed.Add((entry.Segment, entry.LowestVcn));

    /// <summary>
    /// Where the bitmap is, or null where it cannot be located. Reads, through the finished
    /// <paramref name="map"/>, every record the list names for it that is not yet in
    /// <paramref name="visited"/>.
    /// </summary>
    /// <param name="listed">
    /// Whether record 0 has a list. A list names every piece of every attribute, the pieces record 0
    /// holds itself included, so what was found has to be exactly what it names.
    /// </param>
    public MftBitmapPlacement? Locate(
        MftExtentMap map,
        MftSegmentReference self,
        int bytesPerRecord,
        ClusterReader read,
        HashSet<long> visited,
        bool listed)
    {
        var record = new byte[bytesPerRecord];

        foreach (var (segment, _) in _listed)
        {
            if (_broken)
            {
                return null;
            }

            if (segment.Record == self.Record || !visited.Add(segment.Record))
            {
                continue;
            }

            if (!MftExtentMapReader.TryReadExtension(map, segment, self, bytesPerCluster, read, record, out var header))
            {
                return null;
            }

            var attributes = new MftAttributeEnumerator(record.AsSpan(0, header.UsedLength), header.FirstAttributeOffset);

            while (attributes.MoveNext())
            {
                if (attributes.CurrentType == MftRecordParser.AttributeBitmap && MftRecordParser.IsUnnamed(attributes.Current))
                {
                    Offer(attributes.Current, segment);
                }
            }

            if (attributes.IsMalformed)
            {
                return null;
            }
        }

        if (_broken || (listed && !FoundIsListed()))
        {
            return null;
        }

        if (_resident is { } resident)
        {
            return _pieces.Count == 0 ? MftBitmapPlacement.InRecord(resident.Value) : null;
        }

        if (_pieces.Count == 0)
        {
            return null;
        }

        _pieces.Sort(static (a, b) => a.LowestVcn.CompareTo(b.LowestVcn));
        var (runs, clusters, whole) = MftExtentMapReader.Join(_pieces);

        // Only what was written is worth reading: NTFS reads past the initialised size as zeroes
        // without looking at the clusters, which can hold anything.
        var written = Math.Min(_pieces[0].DataSize, _pieces[0].InitializedSize);

        return whole && written >= 0 && (written == 0 || (written - 1) / bytesPerCluster < clusters)
            ? MftBitmapPlacement.InClusters(runs, written)
            : null;
    }

    private bool FoundIsListed()
    {
        var found = _pieces.Select(p => (p.Segment, p.LowestVcn)).ToList();

        if (_resident is { } resident)
        {
            found.Add((resident.Segment, 0));
        }

        return found.Count == _listed.Count && found.TrueForAll(_listed.Contains);
    }
}
