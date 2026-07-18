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
        if (bytesPerSector is < 256 or > 4096 || !int.IsPow2(bytesPerSector))
        {
            return false;
        }

        var sectorsPerCluster = DecodeClusterCount(sector[13]);
        if (sectorsPerCluster <= 0)
        {
            return false;
        }

        var bytesPerCluster = bytesPerSector * sectorsPerCluster;
        var mftStart = BinaryPrimitives.ReadInt64LittleEndian(sector[48..]);
        if (mftStart <= 0)
        {
            return false;
        }

        var bytesPerFileRecord = DecodeRecordSize((sbyte)sector[64], bytesPerCluster);
        if (bytesPerFileRecord < bytesPerSector || !int.IsPow2(bytesPerFileRecord))
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
    /// </summary>
    private static int DecodeClusterCount(byte raw) =>
        raw <= 0x80 ? raw : 1 << (0x100 - raw);

    /// <summary>
    /// The same two-form encoding, for the file record size. In practice every modern volume takes
    /// the negative branch and lands on 1024 bytes, but the positive form is legal and cheap to
    /// honour.
    /// </summary>
    private static int DecodeRecordSize(sbyte raw, int bytesPerCluster) =>
        raw < 0 ? 1 << -raw : raw * bytesPerCluster;
}
