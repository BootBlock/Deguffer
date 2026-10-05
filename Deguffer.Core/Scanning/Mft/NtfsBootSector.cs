using System.Buffers.Binary;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The NTFS BIOS parameter block, which is where a volume says how to find and size its MFT.
///
/// Pure over a 512-byte span so it is testable without a volume handle: the geometry fields here
/// decide how every subsequent byte offset is computed, so getting one wrong misreads the entire
/// file table rather than failing visibly.
/// </summary>
public readonly record struct NtfsBootSector(
    int BytesPerSector,
    int BytesPerCluster,
    long MftStartCluster,
    int BytesPerFileRecord,
    ulong VolumeSerialNumber)
{
    private const int MinimumLength = 512;

    /// <summary>
    /// The largest sector a volume may declare. A read sized to this is a whole number of sectors
    /// on every volume this accepts, which an unbuffered volume read must be.
    /// </summary>
    internal const int MaximumBytesPerSector = 4096;

    /// <summary>
    /// The largest cluster NTFS formats, which Windows has offered since Windows 10 version 1709.
    /// A larger one is damage, and every read the table needs is sized in clusters.
    /// </summary>
    internal const int MaximumBytesPerCluster = 2 * 1024 * 1024;

    /// <summary>NTFS writes this at offset 3; anything else is a different filesystem.</summary>
    private static ReadOnlySpan<byte> OemId => "NTFS    "u8;

    public static bool TryParse(ReadOnlySpan<byte> sector, out NtfsBootSector result)
    {
        result = default;

        if (sector.Length < MinimumLength || !sector.Slice(3, 8).SequenceEqual(OemId))
        {
            return false;
        }

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);

        // A power of two between a 512-byte sector and a 64 KB cluster. Rejecting anything else
        // matters because these values multiply into every later offset, and a nonsense geometry
        // would otherwise produce plausible-looking garbage rather than a clean fallback.
        if (bytesPerSector is < 256 or > MaximumBytesPerSector || !int.IsPow2(bytesPerSector))
        {
            return false;
        }

        // Bounded before the multiplication, so a corrupt count cannot wrap the cluster size to
        // zero and throw from the division below rather than refusing the volume.
        var sectorsPerCluster = DecodeClusterCount(sector[13]);
        if (sectorsPerCluster <= 0 || sectorsPerCluster > MaximumBytesPerCluster / bytesPerSector)
        {
            return false;
        }

        var bytesPerCluster = bytesPerSector * sectorsPerCluster;
        var mftStart = BinaryPrimitives.ReadInt64LittleEndian(sector[48..]);

        // Bounded above as well as below: the opener multiplies it by the cluster size, and a start
        // past any byte a long can address wraps negative there and throws out of the volume read.
        if (mftStart <= 0 || mftStart > long.MaxValue / bytesPerCluster)
        {
            return false;
        }

        // At least one fixup stride as well as one sector: a smaller record has no room for the
        // update sequence array, so not one of its records could be read.
        var bytesPerFileRecord = DecodeRecordSize((sbyte)sector[64], bytesPerCluster);
        if (bytesPerFileRecord < Math.Max(bytesPerSector, UpdateSequenceArray.StrideBytes)
            || !int.IsPow2(bytesPerFileRecord))
        {
            return false;
        }

        result = new NtfsBootSector(
            bytesPerSector,
            bytesPerCluster,
            mftStart,
            bytesPerFileRecord,
            BinaryPrimitives.ReadUInt64LittleEndian(sector[72..]));

        return true;
    }

    /// <summary>
    /// Sectors per cluster is a count up to 128, but volumes formatted with clusters larger than
    /// 64 KB encode it as a signed byte holding the negative base-2 exponent instead.
    ///
    /// <para>An exponent too large for an <c>int</c> answers zero, which the caller refuses. Shifted
    /// as it stands, C# would take it modulo 32, and a byte of 0xA0 would read as one sector.</para>
    /// </summary>
    private static int DecodeClusterCount(byte raw)
    {
        if (raw <= 0x80)
        {
            return raw;
        }

        var exponent = 0x100 - raw;
        return exponent < 31 ? 1 << exponent : 0;
    }

    /// <summary>
    /// The same two-form encoding, for the file record size. In practice every modern volume takes
    /// the negative branch and lands on 1024 bytes, but the positive form is legal and cheap to
    /// honour.
    /// </summary>
    private static int DecodeRecordSize(sbyte raw, int bytesPerCluster) =>
        raw < 0 ? 1 << -raw : raw * bytesPerCluster;
}
