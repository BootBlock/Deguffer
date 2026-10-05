using System.Buffers.Binary;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests;

/// <summary>
/// The fixed part of a non-resident attribute, which a file's size, an attribute list kept outside
/// its record and the table's own extents are all read through. Every field comes off the disk, so
/// a header that cannot be what NTFS writes is refused rather than read.
/// </summary>
public class MftNonResidentHeaderTests
{
    [Fact]
    public void ReadsEachFieldFromItsOwnOffset()
    {
        var attribute = Attribute();

        Assert.True(MftNonResidentHeader.TryRead(attribute, out var header));

        Assert.Equal(new MftNonResidentHeader(
            LowestVcn: 0x11, HighestVcn: 0x22, MappingPairsOffset: 0x40, AllocatedSize: 0x3300, DataSize: 0x2200, InitializedSize: 0x1100), header);
    }

    /// <summary>
    /// The enumerator admits an attribute of 0x10 bytes, far shorter than the header. One that ends
    /// before the data size is refused rather than read past, which would throw out of a scan.
    /// </summary>
    [Theory]
    [InlineData(0x10)]
    [InlineData(0x30)]
    [InlineData(MftNonResidentHeader.Length - 1)]
    public void RefusesAnAttributeShorterThanTheHeader(int length) =>
        Assert.False(MftNonResidentHeader.TryRead(Attribute().AsSpan(0, length), out _));

    [Fact]
    public void RefusesAResidentAttribute()
    {
        var attribute = Attribute();
        attribute[0x08] = 0;

        Assert.False(MftNonResidentHeader.TryRead(attribute, out _));
    }

    [Fact]
    public void RefusesANegativeLowestCluster()
    {
        var attribute = Attribute();
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x10), -1);

        Assert.False(MftNonResidentHeader.TryRead(attribute, out _));
    }

    /// <summary>
    /// A run list starting inside the header would be decoded from the sizes, and one starting at
    /// the attribute's end has no bytes at all. Either reads clusters from somewhere they are not.
    /// </summary>
    [Theory]
    [InlineData(0x38, false)]
    [InlineData(0x3F, false)]
    [InlineData(0x40, true)]
    [InlineData(0x47, true)]
    [InlineData(0x48, false)]
    public void RefusesARunListOutsideTheAttributeBody(int mappingPairsOffset, bool accepted)
    {
        var attribute = Attribute();
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x20), (ushort)mappingPairsOffset);

        Assert.Equal(accepted, MftNonResidentHeader.TryRead(attribute, out _));
    }

    /// <summary>A file's size is read through the same header, so a malformed one leaves the size unknown.</summary>
    [Fact]
    public void LeavesAFileSizeUnknownWhereTheHeaderIsRefused()
    {
        var attribute = Attribute();
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x10), 0);

        Assert.NotNull(MftRecordParser.ReadDataSize(attribute));

        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x20), 0x30);
        Assert.Null(MftRecordParser.ReadDataSize(attribute));
    }

    /// <summary>
    /// A 0x48-byte non-resident <c>$DATA</c> with its run list at 0x40, and every field a distinct
    /// value, so a field read from its neighbour's offset disagrees rather than coinciding.
    /// </summary>
    private static byte[] Attribute()
    {
        var attribute = new byte[0x48];

        BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(0x04), 0x48);
        attribute[0x08] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x10), 0x11);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x18), 0x22);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x28), 0x3300);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x30), 0x2200);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x38), 0x1100);

        return attribute;
    }
}
