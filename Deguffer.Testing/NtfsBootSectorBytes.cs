using System.Buffers.Binary;

namespace Deguffer.Testing;

/// <summary>
/// The fields of an NTFS boot sector the reader looks at, written where NTFS writes them, and
/// nothing else.
/// </summary>
internal static class NtfsBootSectorBytes
{
    public const ulong SerialNumber = 0xDEAD_BEEF_1234_5678;

    /// <param name="clustersPerRecord">
    /// The record size in the format's two encodings: a positive count of clusters, or a negative
    /// power of two in bytes. <see cref="RecordSizeCode"/> chooses the one NTFS writes.
    /// </param>
    public static byte[] Build(ushort bytesPerSector, byte sectorsPerCluster, long mftStart, sbyte clustersPerRecord)
    {
        var sector = new byte[512];

        "NTFS    "u8.CopyTo(sector.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(11), bytesPerSector);
        sector[13] = sectorsPerCluster;
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(48), mftStart);
        sector[64] = (byte)clustersPerRecord;
        BinaryPrimitives.WriteUInt64LittleEndian(sector.AsSpan(72), SerialNumber);

        return sector;
    }

    /// <summary>
    /// A record smaller than a cluster cannot be a whole number of clusters, so NTFS writes its size
    /// as a negative power of two, and a count of clusters otherwise.
    /// </summary>
    public static sbyte RecordSizeCode(int bytesPerRecord, int bytesPerCluster) =>
        bytesPerRecord < bytesPerCluster
            ? (sbyte)-int.Log2(bytesPerRecord)
            : (sbyte)(bytesPerRecord / bytesPerCluster);
}
