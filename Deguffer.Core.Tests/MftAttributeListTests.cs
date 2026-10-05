using System.Buffers.Binary;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reading an <c>$ATTRIBUTE_LIST</c>'s bytes, before anything follows it.
///
/// <para>Every field here comes off the disk, so each test is a corrupt value a reader must refuse
/// rather than throw on or loop over. A throw out of the reader is not a fallback: nothing on the
/// scan path catches it, so the volume never reaches the walk.</para>
/// </summary>
public sealed class MftAttributeListTests
{
    private const uint Data = 0x80;

    [Fact]
    public void ReadsEachEntrysTypeNameStartingClusterAndRecord()
    {
        var value = MftAttributeBytes.AttributeListValue(
        [
            new ListedAttribute(0x30, 21UL | (3UL << 48)),
            new ListedAttribute(Data, 22UL | (4UL << 48), LowestVcn: 7, Name: "stream"),
        ]);

        var entries = MftAttributeList.TryReadEntries(value);

        Assert.Equal(
            [
                new MftAttributeListEntry(0x30, IsNamed: false, LowestVcn: 0, new MftSegmentReference(21, 3)),
                new MftAttributeListEntry(Data, IsNamed: true, LowestVcn: 7, new MftSegmentReference(22, 4)),
            ],
            entries);
    }

    /// <summary>A zero length would read the same entry forever.</summary>
    [Fact]
    public void RefusesAnEntryOfZeroLength()
    {
        var value = OneEntry();
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x04), 0);

        Assert.Null(MftAttributeList.TryReadEntries(value));
    }

    [Fact]
    public void RefusesAnEntryThatRunsPastTheList()
    {
        var value = OneEntry();
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x04), (ushort)(value.Length + 8));

        Assert.Null(MftAttributeList.TryReadEntries(value));
    }

    [Fact]
    public void RefusesANameThatRunsPastItsEntry()
    {
        var value = OneEntry();
        value[0x06] = 0xFF;

        Assert.Null(MftAttributeList.TryReadEntries(value));
    }

    /// <summary>
    /// Bytes left over that cannot hold an entry are a list cut short, and the entry that was cut
    /// may be the one that mattered. Five of them, too few to hold even the entry's length field,
    /// so nothing but the check on what is left can refuse them before the length is read.
    /// </summary>
    [Fact]
    public void RefusesAListEndingPartWayThroughAnEntry()
    {
        var value = OneEntry().Concat(new byte[0x05]).ToArray();

        Assert.Null(MftAttributeList.TryReadEntries(value));
    }

    [Fact]
    public void RefusesANegativeStartingCluster()
    {
        var value = OneEntry();
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0x08), -1);

        Assert.Null(MftAttributeList.TryReadEntries(value));
    }

    /// <summary>
    /// A value length near the top of the range would wrap negative if added to its offset and pass
    /// a bounds check, then throw out of the slice after it.
    /// </summary>
    [Theory]
    [InlineData(0x7FFF_FFF0u)]
    [InlineData(0xFFFF_FFF0u)]
    public void RefusesAResidentValueLongerThanItsAttribute(uint valueLength)
    {
        var attribute = new byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(0x10), valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x14), 0x18);

        Assert.False(MftRecordParser.TryReadResidentValue(attribute, out _));
    }

    /// <summary>
    /// NTFS writes no list longer than 256 KiB. A longer one is damage, and its length would size
    /// the buffer it is read into.
    /// </summary>
    [Fact]
    public void RefusesANonResidentListLongerThanNtfsWrites()
    {
        var attribute = NonResidentList(MftAttributeList.MaximumLength + 1);

        Assert.False(MftAttributeList.TryReadPlacement(attribute, out _, out _));
        Assert.True(MftAttributeList.TryReadPlacement(NonResidentList(MftAttributeList.MaximumLength), out _, out _));
    }

    /// <summary>A list is never split across records, so a piece starting past cluster 0 is damage.</summary>
    [Fact]
    public void RefusesANonResidentListThatDoesNotStartAtClusterZero()
    {
        var attribute = NonResidentList(0x100);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x10), 1);

        Assert.False(MftAttributeList.TryReadPlacement(attribute, out _, out _));
    }

    private static byte[] OneEntry() => MftAttributeBytes.AttributeListValue([new ListedAttribute(Data, 22UL | (1UL << 48))]);

    private static byte[] NonResidentList(int length)
    {
        var attribute = new byte[0x60];
        MftAttributeBytes.WriteNonResidentAttributeList(attribute, startCluster: 900, clusterCount: 65, length);
        return attribute;
    }
}
