using System.Buffers.Binary;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Walks the attribute list of one MFT record, bounds-checking as it goes.
///
/// Shared by the two things that read records — the size parser and the <c>$MFT</c> extent reader —
/// because the walk is where a malformed record turns into an infinite loop or an out-of-bounds
/// read, and that logic should exist once.
/// </summary>
internal ref struct MftAttributeEnumerator
{
    private const uint EndMarker = 0xFFFF_FFFF;

    private readonly ReadOnlySpan<byte> _record;
    private int _offset;

    public MftAttributeEnumerator(ReadOnlySpan<byte> record, int firstAttributeOffset)
    {
        _record = record;
        _offset = firstAttributeOffset;
    }

    public uint CurrentType { get; private set; }

    public ReadOnlySpan<byte> Current { get; private set; }

    /// <summary>
    /// True once the walk has hit a length it cannot honour. Callers must reject the whole record
    /// rather than keeping whatever they read first: a record that fails here is corrupt, and a
    /// partially-parsed one reports a plausible wrong size.
    /// </summary>
    public bool IsMalformed { get; private set; }

    public bool MoveNext()
    {
        if (IsMalformed || _offset < 0 || _offset + 8 > _record.Length)
        {
            return false;
        }

        var type = BinaryPrimitives.ReadUInt32LittleEndian(_record[_offset..]);
        if (type == EndMarker)
        {
            return false;
        }

        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(_record[(_offset + 4)..]);

        // A zero length would spin here forever; an overrunning one would read past the record.
        if (length < 0x10 || _offset + length > _record.Length)
        {
            IsMalformed = true;
            return false;
        }

        CurrentType = type;
        Current = _record.Slice(_offset, length);
        _offset += length;

        return true;
    }
}
