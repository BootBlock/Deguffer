using System.Buffers.Binary;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The fixed part of a non-resident attribute: which of the attribute's virtual clusters this piece
/// describes, where its run list starts, and the sizes the piece at cluster 0 states.
///
/// <para>Read in one place for every attribute that needs it — a file's <c>$DATA</c>, an
/// <c>$ATTRIBUTE_LIST</c> kept outside its record, and <c>$MFT</c>'s own <c>$DATA</c> and
/// <c>$BITMAP</c> — because a field read from the wrong offset still parses, and only a single
/// reader keeps them agreeing on where each field is.</para>
/// </summary>
/// <param name="LowestVcn">The first virtual cluster this piece describes.</param>
/// <param name="HighestVcn">The last. Not checked against the run list here: only a caller that
/// joins pieces together needs it to agree.</param>
/// <param name="AllocatedSize">Zero in every piece but the one at cluster 0, as are the other sizes.</param>
/// <param name="InitializedSize">
/// How much of the data has been written. NTFS reads anything past it as zeroes without reading the
/// disk, so the clusters there can hold anything.
/// </param>
/// <param name="IsCompressedOrSparse">
/// Whether the attribute is compressed or sparse, which is when <paramref name="AllocatedSize"/>
/// stops being what it occupies.
/// </param>
/// <param name="CompressedSize">
/// The clusters a compressed or sparse attribute actually holds, in bytes. Zero for any other.
/// </param>
internal readonly record struct MftNonResidentHeader(
    long LowestVcn,
    long HighestVcn,
    int MappingPairsOffset,
    long AllocatedSize,
    long DataSize,
    long InitializedSize,
    bool IsCompressedOrSparse = false,
    long CompressedSize = 0)
{
    /// <summary>
    /// The header's length as NTFS writes it for an uncompressed attribute. A compressed attribute's
    /// is longer, which its mapping pair offset allows for.
    /// </summary>
    public const int Length = 0x40;

    /// <summary>
    /// The header's length where the attribute is compressed or sparse: NTFS adds the size it
    /// occupies after the other three.
    /// </summary>
    public const int CompressedLength = 0x48;

    private const ushort CompressionMask = 0x00FF;
    private const ushort Sparse = 0x8000;

    /// <summary>
    /// What the attribute occupies on the disk. For a compressed or sparse attribute that is not
    /// <see cref="AllocatedSize"/>, which there is the length rounded up to a compression unit:
    /// a fully sparse 2 MB file states 2 MB allocated and holds no clusters at all.
    /// </summary>
    public long OccupiedSize => IsCompressedOrSparse ? CompressedSize : AllocatedSize;

    public static bool TryRead(ReadOnlySpan<byte> attribute, out MftNonResidentHeader header)
    {
        header = default;

        // The enumerator admits an attribute of 0x10 bytes, so the length is checked before any
        // field is read: an unguarded read throws out of a scan rather than refusing one attribute.
        if (attribute.Length < Length || attribute[0x08] == 0)
        {
            return false;
        }

        var lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..]);
        int mappingPairsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..]);

        // A run list inside the header would be read from the sizes, and one at the end of the
        // attribute is no run list at all.
        if (lowestVcn < 0 || mappingPairsOffset < Length || mappingPairsOffset >= attribute.Length)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x0C..]);
        var compressedOrSparse = (flags & (CompressionMask | Sparse)) != 0;

        // The field that says what such an attribute occupies sits where an ordinary attribute's
        // run list starts. A run list that starts there anyway is a header that is damaged, and
        // reading the size from it would read the runs as a size.
        if (compressedOrSparse && mappingPairsOffset < CompressedLength)
        {
            return false;
        }

        header = new MftNonResidentHeader(
            lowestVcn,
            BinaryPrimitives.ReadInt64LittleEndian(attribute[0x18..]),
            mappingPairsOffset,
            BinaryPrimitives.ReadInt64LittleEndian(attribute[0x28..]),
            BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]),
            BinaryPrimitives.ReadInt64LittleEndian(attribute[0x38..]),
            compressedOrSparse,
            compressedOrSparse ? BinaryPrimitives.ReadInt64LittleEndian(attribute[0x40..]) : 0);
        return true;
    }

    /// <summary>The runs of <paramref name="attribute"/>, the attribute this header was read from.</summary>
    public IReadOnlyList<DataRun> ReadRuns(ReadOnlySpan<byte> attribute) =>
        DataRuns.Parse(attribute[MappingPairsOffset..]);
}
