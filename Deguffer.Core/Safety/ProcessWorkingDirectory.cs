using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>What one read of a process's working directory found.</summary>
/// <param name="Directory">The directory, or null where none could be read.</param>
/// <param name="LayoutUnverified">
/// True where the directory is null because the block it would be read from could not be shown to be
/// the one the process keeps current. Unlike a read that was refused, that is doubt about the
/// mechanism rather than about one process, so the table as a whole stops claiming its working
/// directories were read.
/// </param>
internal readonly record struct WorkingDirectoryRead(string? Directory, bool LayoutUnverified)
{
    public static readonly WorkingDirectoryRead Unread = new(null, false);

    public static readonly WorkingDirectoryRead Unverified = new(null, true);
}

/// <summary>
/// Reads another process's working directory out of its environment block.
///
/// <para><b>A 32-bit process on 64-bit Windows has two blocks, and only one is current.</b> WOW64
/// gives it a 32-bit environment block and parameter block of its own, and its
/// <c>SetCurrentDirectory</c> updates only those. The 64-bit copy keeps whatever it was given at
/// start, and was measured reading the Windows directory for a 32-bit <c>cmd.exe</c> both before and
/// after a <c>cd</c>, while the 32-bit copy read the directory it was started in and then the one it
/// moved to. Read from the 64-bit copy, every 32-bit shell, build and interpreter would be working in
/// the Windows directory, and a project it sat in would read as idle.</para>
///
/// <para><b>The working directory's offset is undocumented, but its neighbours are not.</b>
/// <c>winternl.h</c> declares <c>PEB.ProcessParameters</c> and
/// <c>RTL_USER_PROCESS_PARAMETERS.CommandLine</c>, and leaves <c>CurrentDirectory</c> inside the
/// reserved bytes. The 64-bit offset is checked against Deguffer's own process by
/// <see cref="RunningProcessTable"/>. There is no 32-bit process whose directory Deguffer already
/// knows, so a 32-bit block is checked per process instead: its command line, at the documented
/// offset, must be exactly the one in the 64-bit block. Nothing else at a wrong address reads as the
/// same string, so a match shows the 32-bit environment block and its parameter block were found. The
/// working directory's offset inside that block is the same declaration as the 64-bit one with
/// pointers four bytes wide.</para>
///
/// <para>A 32-bit block that cannot be checked is reported as <see cref="WorkingDirectoryRead.Unverified"/>
/// and never as the 64-bit value, which is the stale one.</para>
/// </summary>
internal static class ProcessWorkingDirectory
{
    private const int ProcessParametersOffset = 0x20;
    private const int CurrentDirectoryOffset = 0x38;
    private const int CommandLineOffset = 0x70;

    private const int Wow64ProcessParametersOffset = 0x10;
    private const int Wow64CurrentDirectoryOffset = 0x24;
    private const int Wow64CommandLineOffset = 0x40;

    /// <summary><c>UNICODE_STRING</c>: two lengths, padding, and an eight-byte pointer.</summary>
    private const int StringHeaderSize = 16;

    /// <summary><c>UNICODE_STRING32</c>: two lengths and a four-byte pointer.</summary>
    private const int Wow64StringHeaderSize = 8;

    /// <summary>A working directory longer than this is not one. It is a misread.</summary>
    private const ushort MaximumPathBytes = 0x8000;

    public static WorkingDirectoryRead Of(IProcessMemory memory)
    {
        if (memory.EnvironmentBlock() is not { } block || block == 0)
        {
            return WorkingDirectoryRead.Unread;
        }

        return memory.Wow64EnvironmentBlock() switch
        {
            // Nothing says which copy is current, and the 64-bit one is stale for a 32-bit process.
            null => WorkingDirectoryRead.Unverified,
            0 => Native(memory, block),
            { } wow64 => Wow64(memory, block, wow64),
        };
    }

    private static WorkingDirectoryRead Native(IProcessMemory memory, long block) =>
        Parameters(memory, block) is { } parameters
        && String(memory, parameters + CurrentDirectoryOffset, MaximumPathBytes) is { } directory
        && Path.IsPathRooted(directory)
            ? new WorkingDirectoryRead(directory, false)
            : WorkingDirectoryRead.Unread;

    private static WorkingDirectoryRead Wow64(IProcessMemory memory, long block, long wow64)
    {
        if (Parameters(memory, block) is not { } parameters
            || Wow64Parameters(memory, wow64) is not { } wow64Parameters
            || String(memory, parameters + CommandLineOffset, ushort.MaxValue) is not { } commandLine
            || Wow64String(memory, wow64Parameters + Wow64CommandLineOffset, ushort.MaxValue) is not { } wow64CommandLine
            || !commandLine.Equals(wow64CommandLine, StringComparison.Ordinal))
        {
            return WorkingDirectoryRead.Unverified;
        }

        // The block was shown to be the process's own, so a directory missing from it or not rooted is
        // a layout that has moved, not a process without one.
        return Wow64String(memory, wow64Parameters + Wow64CurrentDirectoryOffset, MaximumPathBytes) is { } directory
            && Path.IsPathRooted(directory)
                ? new WorkingDirectoryRead(directory, false)
                : WorkingDirectoryRead.Unverified;
    }

    private static long? Parameters(IProcessMemory memory, long block)
    {
        Span<byte> pointer = stackalloc byte[sizeof(ulong)];

        return memory.TryRead(block + ProcessParametersOffset, pointer)
            && (long)BinaryPrimitives.ReadUInt64LittleEndian(pointer) is var address and not 0
                ? address
                : null;
    }

    private static long? Wow64Parameters(IProcessMemory memory, long block)
    {
        Span<byte> pointer = stackalloc byte[sizeof(uint)];

        return memory.TryRead(block + Wow64ProcessParametersOffset, pointer)
            && BinaryPrimitives.ReadUInt32LittleEndian(pointer) is var address and not 0
                ? address
                : null;
    }

    private static string? String(IProcessMemory memory, long address, ushort maximumBytes)
    {
        Span<byte> header = stackalloc byte[StringHeaderSize];

        return memory.TryRead(address, header)
            ? Text(memory, BinaryPrimitives.ReadUInt16LittleEndian(header), (long)BinaryPrimitives.ReadUInt64LittleEndian(header[8..]), maximumBytes)
            : null;
    }

    private static string? Wow64String(IProcessMemory memory, long address, ushort maximumBytes)
    {
        Span<byte> header = stackalloc byte[Wow64StringHeaderSize];

        return memory.TryRead(address, header)
            ? Text(memory, BinaryPrimitives.ReadUInt16LittleEndian(header), BinaryPrimitives.ReadUInt32LittleEndian(header[4..]), maximumBytes)
            : null;
    }

    private static string? Text(IProcessMemory memory, ushort length, long buffer, ushort maximumBytes)
    {
        if (buffer == 0 || length == 0 || length > maximumBytes || length % 2 != 0)
        {
            return null;
        }

        var text = new char[length / 2];

        return memory.TryRead(buffer, MemoryMarshal.AsBytes(text.AsSpan())) ? new string(text) : null;
    }
}
