using System.Text;

namespace Deguffer.Core.Memory;

/// <summary>
/// Strings read out of a buffer Windows filled, by the address it wrote into the same buffer.
///
/// <para>The process table and the service list both hand back records whose strings are pointers
/// into the buffer the caller passed. Dereferencing one directly trusts that the record was parsed at
/// the right offset. Translating it to a position and checking that position against what Windows
/// returned turns a misread into a refused string rather than a read of whatever memory it names.</para>
/// </summary>
internal static class BufferText
{
    /// <summary>
    /// A string of <paramref name="byteLength"/> bytes of UTF-16 at <paramref name="pointer"/>, as a
    /// <c>UNICODE_STRING</c> describes one, or null where it does not lie wholly inside
    /// <paramref name="data"/>.
    /// </summary>
    /// <param name="baseAddress">The address <paramref name="data"/> starts at, which is what the pointer is relative to.</param>
    public static string? Counted(ReadOnlySpan<byte> data, ulong baseAddress, ulong pointer, int byteLength)
    {
        // An empty string carries no address worth checking: the idle process's name has a null one.
        if (byteLength == 0)
        {
            return string.Empty;
        }

        if (byteLength % 2 != 0 || OffsetOf(data, baseAddress, pointer) is not { } offset || data.Length - offset < byteLength)
        {
            return null;
        }

        return Encoding.Unicode.GetString(data.Slice(offset, byteLength));
    }

    /// <summary>
    /// A NUL-terminated UTF-16 string at <paramref name="pointer"/>, or null where it starts outside
    /// <paramref name="data"/> or reaches its end without a terminator.
    /// </summary>
    public static string? Terminated(ReadOnlySpan<byte> data, ulong baseAddress, ulong pointer)
    {
        if (OffsetOf(data, baseAddress, pointer) is not { } offset)
        {
            return null;
        }

        for (var end = offset; end + 1 < data.Length; end += 2)
        {
            if (data[end] == 0 && data[end + 1] == 0)
            {
                return Encoding.Unicode.GetString(data[offset..end]);
            }
        }

        return null;
    }

    /// <summary>
    /// Where <paramref name="pointer"/> falls in <paramref name="data"/>, or null where it falls
    /// outside. One unsigned comparison refuses both sides: an address below the base wraps round to
    /// an offset far past the end.
    /// </summary>
    private static int? OffsetOf(ReadOnlySpan<byte> data, ulong baseAddress, ulong pointer) =>
        unchecked(pointer - baseAddress) < (ulong)data.Length
            ? (int)(pointer - baseAddress)
            : null;
}
