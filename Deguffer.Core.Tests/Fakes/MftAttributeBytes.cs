using System.Buffers.Binary;
using System.Text;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>Where a record keeps the bytes of its unnamed <c>$DATA</c>, and whether they can be read.</summary>
public enum DataPlacement
{
    NonResident,
    Resident,
    NoData,
    InExtensionRecord,
    SplitAcrossExtents,
    LaterExtent,
    TruncatedHeader,
    TruncatedResidentHeader,
}

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
            DataPlacement.InExtensionRecord => WriteAttributeList(target),
            DataPlacement.SplitAcrossExtents => WriteSplitData(target, allocated, logical),
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
    public static int WriteStandardInformation(Span<byte> target, long created, long lastWritten)
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

        return StandardInformationLength;
    }

    public static int WriteFileName(Span<byte> target, ulong parentReference, string name, long allocated, long logical)
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
        value[0x41] = 3; // Win32AndDos
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
    /// A resident <c>$ATTRIBUTE_LIST</c>, standing in for the index NTFS writes when a record's
    /// attributes no longer fit in it. Its contents are not read — what a reader has to notice is
    /// that the record has one at all, and so may be describing itself somewhere else.
    /// </summary>
    public static int WriteAttributeList(Span<byte> target)
    {
        const int Length = 0x28;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], Length);
        target[0x08] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x10..], Length - 0x18);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x14..], 0x18);

        return Length;
    }

    /// <summary>The <c>$DATA</c> of <c>$MFT</c> itself, whose run list says where the table lives.</summary>
    public static int WriteMftData(Span<byte> target, IReadOnlyList<DataRun> runs, long dataSize)
    {
        const int RunsOffset = 0x40;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x80);
        target[0x08] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x20..], RunsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x28..], dataSize);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x30..], dataSize);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x38..], dataSize);

        var cursor = RunsOffset;
        long previous = 0;

        foreach (var run in runs)
        {
            // A four-byte length and a four-byte signed delta: not the most compact encoding NTFS
            // would choose, but a legal one, which is what the reader has to cope with.
            target[cursor++] = 0x44;
            BinaryPrimitives.WriteInt32LittleEndian(target[cursor..], (int)run.ClusterCount);
            cursor += 4;
            BinaryPrimitives.WriteInt32LittleEndian(target[cursor..], (int)(run.StartCluster - previous));
            cursor += 4;
            previous = run.StartCluster;
        }

        target[cursor++] = 0x00;

        var length = Align8(cursor);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);

        return length;
    }

    public static int Align8(int value) => (value + 7) & ~7;

    private static int WriteNonResidentData(Span<byte> target, long allocated, long logical, long startVirtualCluster)
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

    /// <summary>
    /// A file too fragmented for one record: an attribute list, the extent starting at VCN 0 that
    /// carries the sizes, and a continuation extent that does not. NTFS writes them in this order,
    /// and the sizes are still fully known from the first one.
    /// </summary>
    private static int WriteSplitData(Span<byte> target, long allocated, long logical)
    {
        var written = WriteAttributeList(target);
        written += WriteNonResidentData(target[written..], allocated, logical, startVirtualCluster: 0);
        written += WriteNonResidentData(target[written..], allocated: 0, logical: 0, startVirtualCluster: 4);

        return written;
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
