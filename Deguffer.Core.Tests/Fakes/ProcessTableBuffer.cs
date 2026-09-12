using System.Buffers.Binary;
using System.Text;
using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Writes a process table the way <c>NtQuerySystemInformation(SystemProcessInformation)</c> fills one:
/// each record at the structure's offsets, its name straight after it, then room standing in for its
/// thread records, and every pointer an address in the buffer as though the buffer started at
/// <see cref="Base"/>.
///
/// <para><b>The offsets are written out here, not taken from the parser's layout.</b> A fixture that
/// wrote where the parser reads would move with it, and a test built on one could not notice an
/// offset that had moved. These are the positions of <c>SYSTEM_PROCESS_INFORMATION</c>'s fields at
/// each pointer width.</para>
///
/// <para>Each name sits beside its own record rather than at the end of the table, so a test can cut
/// the buffer short inside a later record and still have every earlier one readable.</para>
/// </summary>
internal sealed class ProcessTableBuffer(int pointerSize)
{
    public const int NextEntryOffset = 0x00;
    public const int NumberOfThreads = 0x04;
    public const int WorkingSetPrivateSize = 0x08;
    public const int CreateTime = 0x20;
    public const int NameLength = 0x38;

    /// <summary>What a thread record costs here. Its contents are never read, only stepped over.</summary>
    private const int ThreadRecordSize = 0x50;

    private readonly List<(ProcessRecord Record, int Threads)> _records = [];

    public int RecordSize => pointerSize == 8 ? 0x100 : 0xB8;

    public int NameBuffer => pointerSize == 8 ? 0x40 : 0x3C;

    private int ProcessId => pointerSize == 8 ? 0x50 : 0x44;

    private int ParentProcessId => pointerSize == 8 ? 0x58 : 0x48;

    private int PagefileUsage => pointerSize == 8 ? 0xB8 : 0x7C;

    /// <summary>An address the buffer pretends to start at, of the pointer width being written.</summary>
    public ulong Base => pointerSize == 8 ? 0x0000_7FF6_1000_0000UL : 0x1000_0000UL;

    public ProcessTableBuffer Add(ProcessRecord record, int threads = 2)
    {
        _records.Add((record, threads));
        return this;
    }

    /// <summary>The table, and where each record starts in it.</summary>
    public (byte[] Data, int[] Offsets) Build()
    {
        var offsets = new int[_records.Count];
        var length = 0;

        for (var i = 0; i < _records.Count; i++)
        {
            offsets[i] = length;
            length += Span(_records[i].Record, _records[i].Threads);
        }

        var data = new byte[length];

        for (var i = 0; i < _records.Count; i++)
        {
            var (record, threads) = _records[i];
            var at = offsets[i];
            var slice = data.AsSpan(at);
            var name = Encoding.Unicode.GetBytes(record.Name);
            var next = i + 1 < _records.Count ? offsets[i + 1] - at : 0;

            BinaryPrimitives.WriteUInt32LittleEndian(slice[NextEntryOffset..], (uint)next);
            BinaryPrimitives.WriteUInt32LittleEndian(slice[NumberOfThreads..], (uint)threads);
            BinaryPrimitives.WriteInt64LittleEndian(slice[WorkingSetPrivateSize..], record.PrivateWorkingSet);
            BinaryPrimitives.WriteInt64LittleEndian(slice[CreateTime..], record.CreationTime);
            BinaryPrimitives.WriteUInt16LittleEndian(slice[NameLength..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(slice[(NameLength + 2)..], (ushort)(name.Length + 2));

            WritePointer(slice, NameBuffer, name.Length == 0 ? 0 : Base + (ulong)(at + RecordSize));
            WritePointer(slice, ProcessId, (ulong)record.ProcessId);
            WritePointer(slice, ParentProcessId, (ulong)record.ParentProcessId);
            WritePointer(slice, PagefileUsage, (ulong)record.CommitCharge);

            name.CopyTo(slice[RecordSize..]);
        }

        return (data, offsets);
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

    /// <summary>One record, its name with a terminator, rounded to eight bytes, and its threads.</summary>
    private int Span(ProcessRecord record, int threads)
    {
        var name = (record.Name.Length + 1) * 2;
        return RecordSize + ((name + 7) & ~7) + (threads * ThreadRecordSize);
    }
}
