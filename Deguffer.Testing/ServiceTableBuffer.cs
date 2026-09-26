using System.Buffers.Binary;
using System.Text;
using Deguffer.Core.Memory;

namespace Deguffer.Testing;

/// <summary>
/// Writes the buffer <c>EnumServicesStatusEx</c> fills with <c>SC_ENUM_PROCESS_INFO</c>: every entry at
/// the start, every string after them, and each string pointer an address in the buffer as though it
/// started at <see cref="Base"/>.
///
/// <para>The entry size and the position of the process identifier are written out here rather than
/// taken from the parser, for the reason <see cref="ProcessTableBuffer"/> gives.</para>
/// </summary>
internal sealed class ServiceTableBuffer(int pointerSize)
{
    private readonly List<RunningService> _services = [];

    /// <summary><c>ENUM_SERVICE_STATUS_PROCESSW</c>: two pointers and nine <c>DWORD</c>s, padded to the pointer size.</summary>
    public int EntrySize => pointerSize == 8 ? 56 : 44;

    public ulong Base => pointerSize == 8 ? 0x0000_7FF6_2000_0000UL : 0x2000_0000UL;

    /// <summary><c>dwProcessId</c>, the eighth <c>DWORD</c> after the two pointers.</summary>
    private int ProcessId => (2 * pointerSize) + (7 * sizeof(uint));

    public ServiceTableBuffer Add(RunningService service)
    {
        _services.Add(service);
        return this;
    }

    public byte[] Build()
    {
        var stringsStart = EntrySize * _services.Count;
        var strings = new List<byte>();
        var data = new byte[stringsStart + _services.Sum(s => (s.Name.Length + s.DisplayName.Length + 2) * 2)];

        for (var i = 0; i < _services.Count; i++)
        {
            var slice = data.AsSpan(i * EntrySize, EntrySize);

            WritePointer(slice, 0, Append(strings, _services[i].Name, stringsStart));
            WritePointer(slice, pointerSize, Append(strings, _services[i].DisplayName, stringsStart));
            BinaryPrimitives.WriteUInt32LittleEndian(slice[ProcessId..], (uint)_services[i].ProcessId);
        }

        strings.CopyTo(data, stringsStart);

        return data;
    }

    public void WritePointer(Span<byte> at, int offset, ulong value)
    {
        if (pointerSize == 8)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(at[offset..], value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(at[offset..], checked((uint)value));
        }
    }

    private ulong Append(List<byte> strings, string text, int stringsStart)
    {
        var address = Base + (ulong)(stringsStart + strings.Count);

        strings.AddRange(Encoding.Unicode.GetBytes(text));
        strings.AddRange([0, 0]);

        return address;
    }
}
