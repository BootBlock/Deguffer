using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>
/// One piece of <c>$MFT</c>'s own <c>$DATA</c>, as a fixture writes it.
/// </summary>
/// <param name="Holder">The record the piece is written in: 0, or one of <c>$MFT</c>'s extension records.</param>
/// <param name="LowestVcn">The first cluster of the table the piece describes.</param>
/// <param name="Runs">Where those clusters are on the volume.</param>
/// <param name="HighestVcn">The last cluster the piece declares, or null for the last its runs reach.</param>
internal sealed record MftPiece(long Holder, long LowestVcn, IReadOnlyList<DataRun> Runs, long? HighestVcn = null);

/// <summary>
/// The clusters of a volume whose <c>$MFT</c> is fragmented enough for its run list to outgrow
/// record 0: record 0 lists, in its <c>$ATTRIBUTE_LIST</c>, the extension records holding the rest
/// of <c>$MFT</c>'s <c>$DATA</c>, and every record is written where the table's real layout puts it.
///
/// <para>Clusters are smaller than records here, as on a volume formatted with 512-byte clusters, so a
/// record can begin in one extent and end in another. Only the clusters something was written to can
/// be read, so a reader that looks in the wrong place fails rather than finding zeroes.</para>
/// </summary>
internal sealed class FragmentedMftVolume
{
    public const int BytesPerCluster = 512;

    public const int BytesPerRecord = MftRecordBytes.BytesPerRecord;

    /// <summary>Where a list too long for record 0 is kept, well away from the table.</summary>
    public const long ListCluster = 9_000;

    private readonly Dictionary<long, byte[]> _clusters = [];
    private readonly MftExtentMap _layout;
    private readonly byte[] _record0;

    /// <param name="layout">Where the table's clusters really are, which the records are written through.</param>
    /// <param name="dataSize">The table's size, which the piece at cluster 0 states.</param>
    /// <param name="pieces">The pieces of <c>$MFT</c>'s <c>$DATA</c>, and the records holding them.</param>
    /// <param name="list">
    /// Record 0's list, or null for one entry per piece, naming each piece's holder as it is.
    /// </param>
    /// <param name="listOutside">Whether the list is kept in clusters of its own rather than in record 0.</param>
    /// <param name="extensionBase">The owner every extension record names, or null for record 0 as it is.</param>
    /// <param name="extensionSequence">The sequence number every extension record carries.</param>
    /// <param name="extensionsCarryTheList">Whether every extension record carries a copy of record 0's list.</param>
    public FragmentedMftVolume(
        IReadOnlyList<DataRun> layout,
        long dataSize,
        IReadOnlyList<MftPiece> pieces,
        IReadOnlyList<ListedAttribute>? list = null,
        bool listOutside = false,
        ulong? extensionBase = null,
        ushort extensionSequence = MftRecordBytes.Sequence,
        bool extensionsCarryTheList = false)
    {
        _layout = new MftExtentMap(dataSize, layout);

        var self = Reference(0, MftRecordBytes.Sequence);
        list ??= pieces.Select(p => new ListedAttribute(Data, Reference(p.Holder, MftRecordBytes.Sequence), p.LowestVcn)).ToList();

        _record0 = MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            BytesPerRecord,
            [
                t => MftAttributeBytes.WriteStandardInformation(t, created: 0, lastWritten: 0),
                t => WriteList(t, list, listOutside),
                .. PiecesIn(0, pieces, dataSize),
            ]);
        Place(0, _record0);

        foreach (var holder in pieces.Select(p => p.Holder).Where(h => h != 0).Distinct())
        {
            Place(holder, MftRecordBytes.Compose(
                isDirectory: false,
                extensionBase ?? self,
                extensionSequence,
                BytesPerRecord,
                [
                    .. extensionsCarryTheList ? [t => MftAttributeBytes.WriteAttributeList(t, list)] : Array.Empty<AttributeWriter>(),
                    .. PiecesIn(holder, pieces, dataSize),
                ]));
        }
    }

    /// <summary>A fresh copy of record 0, which the reader's fixup modifies in place.</summary>
    public byte[] Record0 => (byte[])_record0.Clone();

    /// <summary>Whether any read was refused, which tells a refusal by the reader from one by the volume.</summary>
    public bool RefusedARead { get; private set; }

    public bool TryReadClusters(long firstCluster, Span<byte> destination)
    {
        if (destination.Length % BytesPerCluster != 0)
        {
            RefusedARead = true;
            return false;
        }

        for (var i = 0; i < destination.Length / BytesPerCluster; i++)
        {
            if (!_clusters.TryGetValue(firstCluster + i, out var cluster))
            {
                RefusedARead = true;
                return false;
            }

            cluster.CopyTo(destination[(i * BytesPerCluster)..]);
        }

        return true;
    }

    /// <summary>Remove a cluster, as a bad sector would.</summary>
    public void Lose(long cluster) => _clusters.Remove(cluster);

    public static ulong Reference(long record, ushort sequence) => (ulong)record | ((ulong)sequence << 48);

    private const uint Data = 0x80;

    private int WriteList(Span<byte> target, IReadOnlyList<ListedAttribute> list, bool outside)
    {
        if (!outside)
        {
            return MftAttributeBytes.WriteAttributeList(target, list);
        }

        var value = MftAttributeBytes.AttributeListValue(list);
        var clusters = (value.Length + BytesPerCluster - 1) / BytesPerCluster;
        var padded = new byte[clusters * BytesPerCluster];
        value.CopyTo(padded, 0);
        Write(ListCluster, padded);

        return MftAttributeBytes.WriteNonResidentAttributeList(target, ListCluster, clusters, value.Length, BytesPerCluster);
    }

    private static IEnumerable<AttributeWriter> PiecesIn(long holder, IReadOnlyList<MftPiece> pieces, long dataSize) =>
        pieces
            .Where(p => p.Holder == holder)
            .Select(p => (AttributeWriter)(t => MftAttributeBytes.WriteMftData(t, p.Runs, dataSize, p.LowestVcn, p.HighestVcn)));

    /// <summary>Write record <paramref name="number"/> where the table's layout puts it, cluster by cluster.</summary>
    private void Place(long number, byte[] record)
    {
        var offset = number * BytesPerRecord;

        for (var done = 0; done < BytesPerRecord; done += BytesPerCluster)
        {
            if (!_layout.TryTranslate((offset + done) / BytesPerCluster, out var cluster, out _))
            {
                throw new InvalidOperationException($"Record {number} lies outside the table's layout.");
            }

            Write(cluster, record.AsSpan(done, BytesPerCluster).ToArray());
        }
    }

    private void Write(long firstCluster, byte[] bytes)
    {
        for (var i = 0; i < bytes.Length / BytesPerCluster; i++)
        {
            _clusters[firstCluster + i] = bytes.AsSpan(i * BytesPerCluster, BytesPerCluster).ToArray();
        }
    }
}
