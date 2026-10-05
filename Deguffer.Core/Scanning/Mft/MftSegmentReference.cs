namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A file reference as NTFS stores one: a 48-bit record number under the 16-bit sequence number
/// the record had when the reference was written.
///
/// <para>The sequence number is the half that matters when the reference crosses from one record
/// to another. NTFS bumps it every time a record is freed and reused, so a reference whose sequence
/// no longer matches names a record that has since become something else — on a live volume, a
/// file that changed between two reads.</para>
/// </summary>
internal readonly record struct MftSegmentReference(long Record, ushort Sequence)
{
    public static MftSegmentReference FromRaw(ulong raw) =>
        new((long)(raw & 0x0000_FFFF_FFFF_FFFF), (ushort)(raw >> 48));
}
