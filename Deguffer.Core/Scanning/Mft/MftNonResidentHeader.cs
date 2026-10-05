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
internal readonly record struct MftNonResidentHeader(
    long LowestVcn,
    long HighestVcn,
    int MappingPairsOffset,
    long AllocatedSize,
    long DataSize,
    long InitializedSize)
{
    /// <summary>
    /// The header's length as NTFS writes it for an uncompressed attribute. A compressed attribute's
    /// is longer, which its mapping pair offset allows for.
    /// </summary>
    public const int Length = 0x40;

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

        header = new MftNonResidentHeader(
            lowestVcn,
            BinaryPrimitives.ReadInt64LittleEndian(attribute[0x18..]),
            mappingPairsOffset,
            BinaryPrimitives.ReadInt64LittleEndian(attribute[0x28..]),
            BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]),
            BinaryPrimitives.ReadInt64LittleEndian(attribute[0x38..]));
        return true;
    }

    /// <summary>The runs of <paramref name="attribute"/>, the attribute this header was read from.</summary>
    public IReadOnlyList<DataRun> ReadRuns(ReadOnlySpan<byte> attribute) =>
        DataRuns.Parse(attribute[MappingPairsOffset..]);
}
