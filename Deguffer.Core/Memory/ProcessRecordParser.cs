using System.Buffers.Binary;

namespace Deguffer.Core.Memory;

/// <summary>
/// Where the fields this reads sit in one <c>SYSTEM_PROCESS_INFORMATION</c> record, for one pointer
/// size.
///
/// <para>Both forms exist because Deguffer builds for x86 as well as x64 and ARM64, and each process
/// is handed the form of its own width. The offsets were checked against a 64-bit and a 32-bit process
/// on one Windows 11 workstation before this was written. Each one's own record held its exact
/// creation time, its name and its session, and its sizes agreed with its documented counters: exactly
/// in the 64-bit process, and within 4% in the 32-bit one, whose counters were read a moment later.
/// Every service host the service list named was in the table.</para>
///
/// <para>The first three offsets are the same in both, and two of them are undocumented, which is
/// what <see cref="ProcessFigureCheck"/> exists to catch.</para>
/// </summary>
internal sealed record ProcessRecordLayout(
    int PointerSize,
    int RecordSize,
    int NameBuffer,
    int ProcessId,
    int ParentProcessId,
    int PagefileUsage)
{
    public const int NextEntryOffset = 0x00;

    /// <summary>Undocumented: inside the block <c>winternl.h</c> names <c>Reserved1</c>.</summary>
    public const int WorkingSetPrivateSize = 0x08;

    /// <summary>Undocumented, in the same reserved block.</summary>
    public const int CreateTime = 0x20;

    /// <summary><c>ImageName.Length</c>, in bytes.</summary>
    public const int NameLength = 0x38;

    public static readonly ProcessRecordLayout Wide = new(
        PointerSize: 8, RecordSize: 0x100, NameBuffer: 0x40, ProcessId: 0x50, ParentProcessId: 0x58, PagefileUsage: 0xB8);

    public static readonly ProcessRecordLayout Narrow = new(
        PointerSize: 4, RecordSize: 0xB8, NameBuffer: 0x3C, ProcessId: 0x44, ParentProcessId: 0x48, PagefileUsage: 0x7C);

    /// <summary>The form this process is handed.</summary>
    public static ProcessRecordLayout Current => IntPtr.Size == 8 ? Wide : Narrow;

    public ulong PointerAt(ReadOnlySpan<byte> record, int offset) =>
        PointerSize == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(record[offset..])
            : BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
}

/// <summary>One record of the process table, with its undocumented figures as read and not yet checked.</summary>
internal readonly record struct ProcessRecord(
    int ProcessId,
    int ParentProcessId,
    string Name,
    long CommitCharge,
    long PrivateWorkingSet,
    long CreationTime);

/// <param name="Complete">Whether the walk reached the record that ends the table.</param>
internal sealed record ParsedProcessTable(IReadOnlyList<ProcessRecord> Records, bool Complete);

/// <summary>
/// Walks the buffer <c>NtQuerySystemInformation(SystemProcessInformation)</c> filled.
///
/// <para>Every record is followed by one record per thread, so the next process is found only by
/// <c>NextEntryOffset</c>. Every offset and every string is checked against the length Windows
/// returned before anything is read from it, and the walk stops at the first that fails rather than
/// guessing where the next record might be. What it read up to that point is kept, and the table says
/// it is incomplete.</para>
/// </summary>
internal static class ProcessRecordParser
{
    public static ParsedProcessTable Parse(ReadOnlySpan<byte> data, ulong baseAddress, ProcessRecordLayout layout)
    {
        var records = new List<ProcessRecord>();
        var at = 0;

        while (data.Length - at >= layout.RecordSize)
        {
            var record = data.Slice(at, layout.RecordSize);

            var name = BufferText.Counted(
                data,
                baseAddress,
                layout.PointerAt(record, layout.NameBuffer),
                BinaryPrimitives.ReadUInt16LittleEndian(record[ProcessRecordLayout.NameLength..]));

            if (name is null)
            {
                break;
            }

            records.Add(new ProcessRecord(
                ProcessId: (int)layout.PointerAt(record, layout.ProcessId),
                ParentProcessId: (int)layout.PointerAt(record, layout.ParentProcessId),
                Name: name,
                CommitCharge: (long)layout.PointerAt(record, layout.PagefileUsage),
                PrivateWorkingSet: BinaryPrimitives.ReadInt64LittleEndian(record[ProcessRecordLayout.WorkingSetPrivateSize..]),
                CreationTime: BinaryPrimitives.ReadInt64LittleEndian(record[ProcessRecordLayout.CreateTime..])));

            var next = BinaryPrimitives.ReadUInt32LittleEndian(record[ProcessRecordLayout.NextEntryOffset..]);

            if (next == 0)
            {
                return new ParsedProcessTable(records, Complete: true);
            }

            // Shorter than a record would put the next one on top of this one, and the walk could then
            // go round the same bytes for ever. Past the end has nothing to read.
            if (next < layout.RecordSize || next > data.Length - at)
            {
                break;
            }

            at += (int)next;
        }

        return new ParsedProcessTable(records, Complete: false);
    }
}
