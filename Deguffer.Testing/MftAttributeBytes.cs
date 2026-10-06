using System.Buffers.Binary;
using System.Text;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>Where a record keeps the bytes of its unnamed <c>$DATA</c>, and whether they can be read.</summary>
public enum DataPlacement
{
    NonResident,
    Resident,
    NoData,
    LaterExtent,
    TruncatedHeader,
    TruncatedResidentHeader,
}

/// <summary>
/// One line of an <c>$ATTRIBUTE_LIST</c>, as a fixture writes it.
/// </summary>
/// <param name="Type">The listed attribute's type code.</param>
/// <param name="Segment">The record holding it, as a file reference: number, then sequence above.</param>
/// <param name="LowestVcn">The first cluster of the attribute this piece describes.</param>
/// <param name="Name">The attribute's name, or null for the unnamed one.</param>
internal readonly record struct ListedAttribute(uint Type, ulong Segment, long LowestVcn = 0, string? Name = null);

/// <summary>
/// One attribute at a time, encoded as NTFS encodes it.
///
/// Separate from <see cref="MftRecordBytes"/> for the reason that one is separate from
/// <see cref="MftFixture"/>: laying out an attribute and assembling a record out of several are
/// different jobs, and only the first has to know what any given attribute's fields mean.
/// </summary>
internal static class MftAttributeBytes
{
    /// <summary>
    /// How long a <see cref="WriteStandardInformation"/> attribute is, header included.
    ///
    /// <para>Exposed because it sits before every other attribute in a record, so anything working
    /// out where a later field lands has to allow for it —
    /// <see cref="MftRecordBytes.NameLengthPuttingSizeFieldAcrossBoundary"/> is the one that
    /// does.</para>
    /// </summary>
    public const int StandardInformationLength = 0x18 + 0x48;

    /// <summary>
    /// The two times in <c>$STANDARD_INFORMATION</c> that must never be read as the two that are.
    /// Distinct values, and distinct from anything a test asks for, so picking the wrong field
    /// fails rather than coinciding. Arbitrary instants in 2001 and 2002.
    /// </summary>
    private const long RecordChangedFileTime = 126_200_000_000_000_000L;

    private const long LastReadFileTime = 126_500_000_000_000_000L;

    /// <summary>
    /// The unnamed <c>$DATA</c>, in whichever of its on-disk forms is asked for. Returns the number
    /// of bytes written, which is zero where the shape is the absence of the attribute.
    /// </summary>
    public static int WriteData(Span<byte> target, long allocated, long logical, DataPlacement placement) =>
        placement switch
        {
            DataPlacement.Resident => WriteResidentData(target, (int)logical),
            DataPlacement.NoData => 0,
            DataPlacement.LaterExtent => WriteNonResidentData(target, allocated, logical, startVirtualCluster: 4),
            DataPlacement.TruncatedHeader => WriteTruncatedData(target, 0x30, resident: false),
            DataPlacement.TruncatedResidentHeader => WriteTruncatedData(target, 0x10, resident: true),
            _ => WriteNonResidentData(target, allocated, logical, startVirtualCluster: 0),
        };

    /// <summary>
    /// The <c>$STANDARD_INFORMATION</c> every record on a real volume carries, holding the four
    /// times NTFS keeps. Only the first two are given values here, because only the first two are
    /// read.
    ///
    /// <para>The value is written at the NTFS 3.x length of 0x48 bytes rather than the 0x30 of the
    /// original format. A reader that assumed the shorter one would still pass against a fixture
    /// that wrote it, and every volume Deguffer will meet is the longer.</para>
    ///
    /// <para><b>Deliberately the first attribute of the record</b>, which is where NTFS puts it —
    /// see <see cref="StandardInformationLength"/> for what depends on that.</para>
    /// </summary>
    /// <param name="attributes">
    /// The file attributes NTFS keeps beside the times: the same bits a directory listing reports,
    /// cloud, compressed and sparse among them.
    /// </param>
    public static int WriteStandardInformation(
        Span<byte> target, long created, long lastWritten, FileAttributes attributes = default)
    {
        const int ValueLength = 0x48;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], StandardInformationLength);
        target[0x08] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x0A..], 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x10..], ValueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x14..], 0x18);

        var value = target.Slice(0x18, ValueLength);

        BinaryPrimitives.WriteInt64LittleEndian(value, created);
        BinaryPrimitives.WriteInt64LittleEndian(value[0x08..], lastWritten);

        // The other two NTFS keeps: when the record last changed, and when the file was last read.
        // Written to values nothing else here uses, so a reader that took the wrong field would
        // produce a date no test asked for rather than one that happens to match.
        BinaryPrimitives.WriteInt64LittleEndian(value[0x10..], RecordChangedFileTime);
        BinaryPrimitives.WriteInt64LittleEndian(value[0x18..], LastReadFileTime);
        BinaryPrimitives.WriteUInt32LittleEndian(value[0x20..], (uint)attributes);

        return StandardInformationLength;
    }

    /// <param name="nameSpace">
    /// Which namespace the name is in: 0 Posix, 1 Win32, 2 DOS, 3 both Win32 and DOS. The last is
    /// what an ordinary short name gets, so it is the default.
    /// </param>
    public static int WriteFileName(
        Span<byte> target, ulong parentReference, string name, long allocated, long logical, byte nameSpace = 3)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var valueLength = 0x42 + nameBytes.Length;
        var length = Align8(0x18 + valueLength);

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);
        target[0x08] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x0A..], 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x10..], (uint)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x14..], 0x18);

        var value = target.Slice(0x18, valueLength);

        BinaryPrimitives.WriteUInt64LittleEndian(value, parentReference);
        BinaryPrimitives.WriteInt64LittleEndian(value[0x28..], allocated);
        BinaryPrimitives.WriteInt64LittleEndian(value[0x30..], logical);
        value[0x40] = (byte)name.Length;
        value[0x41] = nameSpace;
        nameBytes.CopyTo(value[0x42..]);

        return length;
    }

    /// <summary>
    /// The <c>$REPARSE_POINT</c> that makes an entry a junction or a link. Its contents are the tag
    /// and the target, neither of which anything here reads: what makes this entry a link is that
    /// the attribute is present at all.
    ///
    /// Deliberately not written as a flag beside the name. The flags in <c>$FILE_NAME</c> are a
    /// copy NTFS refreshes when the name changes rather than when the file does, so a fixture that
    /// set one there would be agreeing with a reader that looked in the same wrong place.
    /// </summary>
    public static int WriteReparsePoint(Span<byte> target, uint tag)
    {
        const int Length = 0x28;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0xC0);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], Length);
        target[0x08] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x10..], Length - 0x18);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x14..], 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x18..], tag);

        return Length;
    }

    /// <summary>
    /// A resident <c>$ATTRIBUTE_LIST</c>: the index NTFS writes when a record's attributes no longer
    /// fit in it, saying which record each one moved to.
    /// </summary>
    public static int WriteAttributeList(Span<byte> target, IReadOnlyList<ListedAttribute> entries) =>
        WriteAttributeListValue(target, AttributeListValue(entries));

    /// <summary>A resident list holding exactly <paramref name="value"/>, whether or not it reads as one.</summary>
    public static int WriteAttributeListValue(Span<byte> target, ReadOnlySpan<byte> value)
    {
        var length = Align8(0x18 + value.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);
        target[0x08] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x10..], (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x14..], 0x18);
        value.CopyTo(target[0x18..]);

        return length;
    }

    /// <summary>
    /// A list grown too large for its record, which NTFS then keeps in clusters of its own outside
    /// the table. <paramref name="length"/> bytes of it, starting at <paramref name="startCluster"/>
    /// and running for <paramref name="clusterCount"/> clusters.
    /// </summary>
    /// <param name="startCluster">
    /// Null for a sparse run: clusters with no place on the disk, which read as zeroes and so hold
    /// none of the list's entries.
    /// </param>
    /// <param name="bytesPerCluster">The volume's cluster size, which the allocated size is counted in.</param>
    public static int WriteNonResidentAttributeList(
        Span<byte> target, long? startCluster, int clusterCount, int length, int bytesPerCluster = MftRecordBytes.BytesPerCluster)
    {
        const int RunsOffset = 0x40;
        const int Length = RunsOffset + 16;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], Length);
        target[0x08] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(target[0x18..], clusterCount - 1);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x20..], RunsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x28..], (long)clusterCount * bytesPerCluster);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x30..], length);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x38..], length);

        // One run: a four-byte length and a four-byte start, then the terminating zero. A sparse run
        // has a length and no start.
        var runs = target[RunsOffset..];
        BinaryPrimitives.WriteInt32LittleEndian(runs[1..], clusterCount);

        if (startCluster is { } start)
        {
            runs[0] = 0x44;
            BinaryPrimitives.WriteInt32LittleEndian(runs[5..], (int)start);
            runs[9] = 0;
        }
        else
        {
            runs[0] = 0x04;
            runs[5] = 0;
        }

        return Length;
    }

    /// <summary>The entries of a list, laid end to end as NTFS lays them.</summary>
    public static byte[] AttributeListValue(IReadOnlyList<ListedAttribute> entries)
    {
        const int NameOffset = 0x1A;

        var lengths = entries.Select(e => Align8(NameOffset + ((e.Name?.Length ?? 0) * 2))).ToArray();
        var value = new byte[lengths.Sum()];
        var offset = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var target = value.AsSpan(offset, lengths[i]);

            BinaryPrimitives.WriteUInt32LittleEndian(target, entry.Type);
            BinaryPrimitives.WriteUInt16LittleEndian(target[0x04..], (ushort)lengths[i]);
            target[0x06] = (byte)(entry.Name?.Length ?? 0);
            target[0x07] = NameOffset;
            BinaryPrimitives.WriteInt64LittleEndian(target[0x08..], entry.LowestVcn);
            BinaryPrimitives.WriteUInt64LittleEndian(target[0x10..], entry.Segment);
            BinaryPrimitives.WriteUInt16LittleEndian(target[0x18..], (ushort)i);

            if (entry.Name is { } name)
            {
                Encoding.Unicode.GetBytes(name).CopyTo(target[NameOffset..]);
            }

            offset += lengths[i];
        }

        return value;
    }

    /// <summary>
    /// A piece of the <c>$DATA</c> of <c>$MFT</c> itself, whose run list says where the table lives:
    /// the whole of it, or, on a table fragmented enough to need extension records, the piece
    /// starting at <paramref name="lowestVcn"/>. Only the piece at cluster 0 states the sizes.
    /// </summary>
    /// <param name="highestVcn">
    /// The last cluster the piece declares, or null for the last cluster its runs reach, which is
    /// what NTFS writes.
    /// </param>
    public static int WriteMftData(
        Span<byte> target, IReadOnlyList<DataRun> runs, long dataSize, long lowestVcn = 0, long? highestVcn = null) =>
        WriteMftNonResident(target, 0x80, runs, dataSize, dataSize, lowestVcn, highestVcn);

    /// <summary>
    /// A piece of <c>$MFT</c>'s own <c>$BITMAP</c>, one bit a record, kept in clusters of its own as
    /// it is on every volume but a tiny one. Only the piece at cluster 0 states the sizes.
    /// </summary>
    /// <param name="initializedSize">How much of it NTFS has written, or null for all of it.</param>
    public static int WriteMftBitmap(
        Span<byte> target, IReadOnlyList<DataRun> runs, long length, long lowestVcn = 0, long? initializedSize = null) =>
        WriteMftNonResident(target, 0xB0, runs, length, initializedSize ?? length, lowestVcn, highestVcn: null);

    /// <summary><c>$MFT</c>'s own <c>$BITMAP</c>, kept inside its record, as a tiny table's is.</summary>
    public static int WriteResidentMftBitmap(Span<byte> target, ReadOnlySpan<byte> value)
    {
        var length = Align8(0x18 + value.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0xB0);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);
        target[0x08] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x10..], (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x14..], 0x18);
        value.CopyTo(target[0x18..]);

        return length;
    }

    private static int WriteMftNonResident(
        Span<byte> target,
        uint type,
        IReadOnlyList<DataRun> runs,
        long dataSize,
        long initializedSize,
        long lowestVcn,
        long? highestVcn)
    {
        const int RunsOffset = 0x40;

        var sizes = lowestVcn == 0 ? dataSize : 0;

        BinaryPrimitives.WriteUInt32LittleEndian(target, type);
        target[0x08] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(target[0x10..], lowestVcn);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x18..], highestVcn ?? (lowestVcn + runs.Sum(r => r.ClusterCount) - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x20..], RunsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x28..], sizes);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x30..], sizes);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x38..], lowestVcn == 0 ? initializedSize : 0);

        var cursor = RunsOffset;
        long previous = 0;

        foreach (var run in runs)
        {
            // An eight-byte length and an eight-byte signed delta: not the most compact encoding NTFS
            // would choose, but a legal one, which is what the reader has to cope with — and the
            // only one wide enough to state a cluster past what a byte offset can address.
            target[cursor++] = 0x88;
            BinaryPrimitives.WriteInt64LittleEndian(target[cursor..], run.ClusterCount);
            cursor += 8;
            BinaryPrimitives.WriteInt64LittleEndian(target[cursor..], run.StartCluster - previous);
            cursor += 8;
            previous = run.StartCluster;
        }

        target[cursor++] = 0x00;

        var length = Align8(cursor);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);

        return length;
    }

    public static int Align8(int value) => (value + 7) & ~7;

    /// <summary>
    /// One piece of a non-resident unnamed <c>$DATA</c>. Only the piece starting at cluster 0
    /// states the sizes on a real volume, so a later piece is written with zeroes in them.
    /// </summary>
    public static int WriteNonResidentData(Span<byte> target, long allocated, long logical, long startVirtualCluster)
    {
        const int Length = 0x48;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], Length);
        target[0x08] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(target[0x10..], (ulong)startVirtualCluster);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x20..], 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x28..], allocated);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x30..], logical);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x38..], logical);

        return Length;
    }

    /// <summary>The flag NTFS sets on a compressed attribute: its compression unit's format.</summary>
    public const ushort CompressedFlag = 0x0001;

    /// <summary>The flag NTFS sets on a sparse attribute.</summary>
    public const ushort SparseFlag = 0x8000;

    /// <summary>
    /// A non-resident <c>$DATA</c> starting at cluster 0, named or not, compressed or sparse or
    /// neither, as NTFS writes each.
    ///
    /// <para>A compressed or sparse one has a longer header: the clusters it actually holds follow
    /// the three sizes, and its run list moves along to make room. That field, and not the
    /// allocated size, is what such a stream occupies. NTFS states the allocated size of a fully
    /// sparse 2 MB file as 2 MB.</para>
    /// </summary>
    /// <param name="occupied">The clusters held, in bytes. Written only where a flag is set.</param>
    public static int WriteStream(
        Span<byte> target, string? name, long allocated, long logical, ushort flags = 0, long occupied = 0)
    {
        var header = flags == 0 ? 0x40 : 0x48;
        var nameBytes = (name?.Length ?? 0) * 2;
        var runs = Align8(header + nameBytes);
        var length = runs + 8;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);
        target[0x08] = 1;
        target[0x09] = (byte)(name?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x0A..], (ushort)header);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x0C..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x20..], (ushort)runs);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x28..], allocated);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x30..], logical);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x38..], logical);

        if (flags != 0)
        {
            BinaryPrimitives.WriteInt64LittleEndian(target[0x40..], occupied);
        }

        if (name is not null)
        {
            Encoding.Unicode.GetBytes(name, target.Slice(header, nameBytes));
        }

        target.Slice(runs, 8).Clear();

        return length;
    }

    /// <summary>
    /// A <c>$DATA</c> whose declared length stops before the fields a reader wants from it. The
    /// enumerator admits any attribute of at least 0x10 bytes, so a reader that indexes past that
    /// without checking throws rather than reporting an unknown size.
    /// </summary>
    private static int WriteTruncatedData(Span<byte> target, int length, bool resident)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);
        target[0x08] = (byte)(resident ? 0 : 1);

        return length;
    }

    private static int WriteResidentData(Span<byte> target, int valueLength)
    {
        var length = Align8(0x18 + valueLength);

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);
        target[0x08] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x10..], (uint)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x14..], 0x18);

        return length;
    }
}
