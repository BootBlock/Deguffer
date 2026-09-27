using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>One entry of the system's process list.</summary>
internal readonly record struct ListedProcess(int Id, string Name);

/// <summary>
/// Another process's memory, and the two questions that say where its environment blocks are.
/// </summary>
internal interface IProcessMemory
{
    /// <summary>
    /// <c>PROCESS_BASIC_INFORMATION.PebBaseAddress</c>: the environment block of Deguffer's own width,
    /// or null where Windows would not say.
    /// </summary>
    long? EnvironmentBlock();

    /// <summary>
    /// <c>ProcessWow64Information</c>: zero for a process of Deguffer's own width, the address of its
    /// 32-bit environment block for a WOW64 process, or null where Windows would not say.
    /// </summary>
    long? Wow64EnvironmentBlock();

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="address"/>, or says it could not.</summary>
    bool TryRead(long address, Span<byte> buffer);
}

/// <summary>One open process, for as long as the caller holds it.</summary>
internal interface IOpenedProcess : IDisposable
{
    /// <summary>Where its executable lives, or null where that could not be read.</summary>
    string? ImagePath();

    /// <summary>The command line it was started with, or null where that could not be read.</summary>
    string? CommandLine();

    /// <summary>Its memory, or null where the open was not granted the right to read it.</summary>
    IProcessMemory? Memory { get; }
}

/// <summary>
/// The Windows calls <see cref="RunningProcessTable"/> is read through, as a seam a test can drive:
/// this machine cannot be made to show a 32-bit process whose block does not check out, or a layout
/// that has moved.
/// </summary>
internal interface IProcessTableCalls
{
    /// <summary>Every process the system lists, skipping any that exited while being listed.</summary>
    IReadOnlyList<ListedProcess> List();

    /// <summary>
    /// Opens <paramref name="processId"/>, asking to read its memory as well where
    /// <paramref name="withMemory"/> is set, or null where it could not be opened at all.
    /// </summary>
    IOpenedProcess? Open(int processId, bool withMemory);
}

/// <inheritdoc />
internal sealed partial class ProcessTableCalls : IProcessTableCalls
{
    public static readonly ProcessTableCalls Instance = new();

    private const uint QueryLimitedInformation = 0x1000;
    private const uint VmRead = 0x0010;

    private const int BasicInformationClass = 0;
    private const int Wow64InformationClass = 26;

    /// <summary>
    /// <c>ProcessCommandLineInformation</c>. Unlike the working directory this needs no offsets: the
    /// kernel copies the command line out itself, and asks only for limited query access.
    /// </summary>
    private const int CommandLineInformationClass = 60;

    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    private ProcessTableCalls()
    {
    }

    public IReadOnlyList<ListedProcess> List()
    {
        var listed = new List<ListedProcess>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    listed.Add(new ListedProcess(process.Id, process.ProcessName));
                }
                catch (InvalidOperationException)
                {
                    // Exited between enumeration and inspection. Normal; skip it.
                }
            }
        }

        return listed;
    }

    public IOpenedProcess? Open(int processId, bool withMemory)
    {
        // The environment-block layouts read through IProcessMemory are the 64-bit ones, and a 32-bit
        // process cannot address another's 64-bit memory. So a 32-bit Deguffer reads no working
        // directory, and the layout self-check reports that rather than a misread.
        if (withMemory && Environment.Is64BitProcess)
        {
            var full = OpenProcess(QueryLimitedInformation | VmRead, false, (uint)processId);

            if (full != 0)
            {
                return new OpenedProcess(full, readable: true);
            }
        }

        // Reading memory is refused more often than reading the image path, and the image path on
        // its own still answers the .venv case.
        var limited = OpenProcess(QueryLimitedInformation, false, (uint)processId);

        return limited == 0 ? null : new OpenedProcess(limited, readable: false);
    }

    private sealed class OpenedProcess(nint handle, bool readable) : IOpenedProcess, IProcessMemory
    {
        public IProcessMemory? Memory => readable ? this : null;

        public string? ImagePath()
        {
            const int characters = 1024;
            var buffer = Marshal.AllocHGlobal(characters * sizeof(char));

            try
            {
                var size = (uint)characters;

                return QueryFullProcessImageName(handle, 0, buffer, ref size)
                    ? Marshal.PtrToStringUni(buffer, (int)size)
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public string? CommandLine()
        {
            if (NtQueryInformationProcess(handle, CommandLineInformationClass, 0, 0, out var needed) != StatusInfoLengthMismatch
                || needed < Marshal.SizeOf<UnicodeString>())
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(needed);

            try
            {
                if (NtQueryInformationProcess(handle, CommandLineInformationClass, buffer, needed, out _) != 0)
                {
                    return null;
                }

                // The string's own buffer is inside the one handed in, straight after its header.
                var line = Marshal.PtrToStructure<UnicodeString>(buffer);

                return line.Buffer == 0 || line.Length == 0 || line.Length % 2 != 0
                    ? null
                    : Marshal.PtrToStringUni(line.Buffer, line.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public long? EnvironmentBlock() =>
            NtQueryBasicInformation(handle, BasicInformationClass, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? basic.ProcessEnvironmentBlock
                : null;

        public long? Wow64EnvironmentBlock() =>
            NtQueryWow64Information(handle, Wow64InformationClass, out var block, IntPtr.Size, out _) == 0
                ? block
                : null;

        public bool TryRead(long address, Span<byte> buffer) =>
            ReadProcessMemory(handle, (nint)address, buffer, buffer.Length, out var read) && read == buffer.Length;

        public void Dispose() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint ProcessEnvironmentBlock;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint ParentProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(nint process, nint address, Span<byte> buffer, nint size, out nint read);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, nint buffer, ref uint size);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        nint process,
        int informationClass,
        nint information,
        int length,
        out int returned);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static partial int NtQueryBasicInformation(
        nint process,
        int informationClass,
        out ProcessBasicInformation information,
        int length,
        out int returned);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static partial int NtQueryWow64Information(
        nint process,
        int informationClass,
        out nint information,
        int length,
        out int returned);
}
