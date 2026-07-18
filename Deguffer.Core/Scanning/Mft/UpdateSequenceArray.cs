using System.Buffers.Binary;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Undoes NTFS's update sequence array — the torn-write detector stamped into every MFT record.
///
/// NTFS overwrites the last two bytes of each sector in a record with a per-record sequence number
/// and stashes the displaced originals in an array at the record's head. A reader that skips this
/// step gets a record whose bytes are *almost* right: two bytes per sector are wrong, and if one of
/// them lands inside a size field the record reports a plausible but wildly incorrect number. That
/// is precisely the class of silent error §5.5's scanner cannot afford, which is why this is a
/// separate step with its own tests rather than a few lines inside the parser.
/// </summary>
public static class UpdateSequenceArray
{
    /// <summary>
    /// Restore <paramref name="record"/> in place. Returns false if the record's own header
    /// describes an array that does not fit, or if any sector's stamp disagrees with the sequence
    /// number — the latter means a genuinely torn write, and the record must not be trusted.
    /// </summary>
    public static bool TryApply(Span<byte> record, int offset, int count, int bytesPerSector)
    {
        // count includes the sequence number itself, so a valid array covers count-1 sectors.
        if (count < 1 || offset < 0 || offset + (count * 2) > record.Length)
        {
            return false;
        }

        var sectors = count - 1;
        if (sectors * bytesPerSector > record.Length)
        {
            return false;
        }

        var array = record.Slice(offset, count * 2);
        var stamp = BinaryPrimitives.ReadUInt16LittleEndian(array);

        for (var i = 0; i < sectors; i++)
        {
            var tail = record.Slice(((i + 1) * bytesPerSector) - 2, 2);

            if (BinaryPrimitives.ReadUInt16LittleEndian(tail) != stamp)
            {
                return false;
            }

            array.Slice((i + 1) * 2, 2).CopyTo(tail);
        }

        return true;
    }
}
