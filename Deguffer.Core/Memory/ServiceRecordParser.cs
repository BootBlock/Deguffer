using System.Buffers.Binary;

namespace Deguffer.Core.Memory;

/// <summary>
/// Reads the <c>ENUM_SERVICE_STATUS_PROCESSW</c> entries <c>EnumServicesStatusEx</c> wrote at the
/// start of its buffer, whose two strings point further into the same buffer.
/// </summary>
internal static class ServiceRecordParser
{
    /// <summary>
    /// Where <c>SERVICE_STATUS_PROCESS.dwProcessId</c> sits after the two string pointers: it is the
    /// eighth of that structure's nine <c>DWORD</c>s.
    /// </summary>
    private const int ProcessIdAfterPointers = 7 * sizeof(uint);

    /// <summary>
    /// Two pointers and nine <c>DWORD</c>s, padded to the pointer size: 56 bytes in a 64-bit process
    /// and 44 in a 32-bit one. Checked against both on Windows 11.
    /// </summary>
    public static int EntrySize(int pointerSize) =>
        pointerSize == 8 ? 56 : (2 * pointerSize) + (9 * sizeof(uint));

    /// <summary>
    /// Add the <paramref name="count"/> entries at the start of <paramref name="data"/> to
    /// <paramref name="services"/>, and say whether every one of them could be read.
    ///
    /// <para>A service reported with no process is left out: it is between states, and a picture of
    /// memory has nothing to attach it to.</para>
    /// </summary>
    /// <returns>
    /// False where the entries run past the buffer or a name does not lie inside it. The read stops
    /// there, so the entries before it are added and none after it.
    /// </returns>
    public static bool Parse(
        ReadOnlySpan<byte> data, ulong baseAddress, int count, int pointerSize, List<RunningService> services)
    {
        var size = EntrySize(pointerSize);

        if ((long)count * size > data.Length)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var entry = data.Slice(i * size, size);

            var name = BufferText.Terminated(data, baseAddress, PointerAt(entry, 0, pointerSize));
            var displayName = BufferText.Terminated(data, baseAddress, PointerAt(entry, pointerSize, pointerSize));

            if (name is null || displayName is null)
            {
                return false;
            }

            var processId = BinaryPrimitives.ReadUInt32LittleEndian(entry[((2 * pointerSize) + ProcessIdAfterPointers)..]);

            if (processId != 0)
            {
                services.Add(new RunningService(name, displayName, (int)processId));
            }
        }

        return true;
    }

    private static ulong PointerAt(ReadOnlySpan<byte> entry, int offset, int pointerSize) =>
        pointerSize == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(entry[offset..])
            : BinaryPrimitives.ReadUInt32LittleEndian(entry[offset..]);
}
