using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <param name="Name">The process name, for telling the user what to close.</param>
/// <param name="ImagePath">Where its executable lives, or null where that could not be read.</param>
/// <param name="CurrentDirectory">Its working directory, or null where that could not be read.</param>
internal sealed record RunningProcess(string Name, string? ImagePath, string? CurrentDirectory);

/// <param name="Processes">Every process this account was allowed to look at.</param>
/// <param name="CurrentDirectoriesReadable">
/// Whether working directories could be read at all. False means the layout self-check failed, so
/// every <see cref="RunningProcess.CurrentDirectory"/> is null because nothing could be read rather
/// than because the processes have none.
/// </param>
internal sealed record ProcessTable(IReadOnlyList<RunningProcess> Processes, bool CurrentDirectoriesReadable);

/// <summary>
/// One pass over the process table, answering the two questions that can be asked about a directory
/// rather than about a file.
///
/// <list type="bullet">
/// <item>Is a running program's <b>executable</b> inside it? That is a <c>.venv</c> whose interpreter
/// is running, or a binary started from <c>target\debug</c>.</item>
/// <item>Is a running program's <b>working directory</b> inside the project? That is a build in
/// flight, a shell sitting in the project, or an editor with the solution open — Visual Studio's
/// working directory is the solution's own folder, observed rather than assumed.</item>
/// </list>
///
/// <para>Both are readable without elevation for every process this account owns, and one pass over
/// roughly five hundred processes costs about thirty milliseconds — measured before this was
/// written, on the machine <c>docs/todo/unreached-locations.md</c> §2 was written against.</para>
///
/// <para><b>The working directory has no documented accessor</b>, so it is read out of the process
/// environment block at offsets Windows does not promise to keep. A layout that moved would produce
/// nonsense matching no directory, which reads as "nothing is using this" — the one wrong answer
/// that costs somebody their work. So the offsets are checked against this process, whose own
/// working directory is already known, and a mismatch turns the mechanism off and says so rather
/// than quietly reporting an empty result.</para>
/// </summary>
internal static partial class RunningProcessTable
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint VmRead = 0x0010;

    /// <summary>
    /// Offsets into the 64-bit process environment block: <c>PEB.ProcessParameters</c>, then
    /// <c>RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath</c>. Both are undocumented, which is
    /// what <see cref="LayoutIsSound"/> exists to catch.
    /// </summary>
    private const int ProcessParametersOffset = 0x20;
    private const int CurrentDirectoryOffset = 0x38;

    /// <summary>A working directory longer than this is not one — it is a misread.</summary>
    private const ushort MaximumPathBytes = 0x8000;

    public static ProcessTable Read(CancellationToken ct = default)
    {
        var readable = LayoutIsSound();
        var processes = new List<RunningProcess>();

        foreach (var process in Process.GetProcesses())
        {
            ct.ThrowIfCancellationRequested();

            using (process)
            {
                string name;
                int id;

                try
                {
                    name = process.ProcessName;
                    id = process.Id;
                }
                catch (InvalidOperationException)
                {
                    // Exited between enumeration and inspection. Normal; skip it.
                    continue;
                }

                var handle = Open((uint)id, readable);

                if (handle == 0)
                {
                    // A process of another account, or a protected one. Neither is the developer's
                    // own editor or build, which is the only thing this is looking for.
                    continue;
                }

                try
                {
                    processes.Add(new RunningProcess(
                        name,
                        ImagePathOf(handle),
                        readable ? CurrentDirectoryOf(handle) : null));
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }

        return new ProcessTable(processes, readable);
    }

    private static nint Open(uint id, bool wantMemory)
    {
        if (wantMemory)
        {
            var full = OpenProcess(QueryLimitedInformation | VmRead, false, id);

            if (full != 0)
            {
                return full;
            }
        }

        // Reading memory is refused more often than reading the image path, and the image path on
        // its own still answers the .venv case.
        return OpenProcess(QueryLimitedInformation, false, id);
    }

    /// <summary>
    /// Whether the environment-block offsets still describe this Windows, checked against the one
    /// process whose working directory is already known.
    /// </summary>
    private static bool LayoutIsSound()
    {
        var handle = OpenProcess(QueryLimitedInformation | VmRead, false, (uint)Environment.ProcessId);

        if (handle == 0)
        {
            return false;
        }

        try
        {
            if (CurrentDirectoryOf(handle) is not { } read)
            {
                return false;
            }

            // Windows stores it with a trailing separator; Environment does not.
            return Path.TrimEndingDirectorySeparator(read).Equals(
                Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? ImagePathOf(nint handle)
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

    private static string? CurrentDirectoryOf(nint handle)
    {
        if (NtQueryInformationProcess(handle, 0, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0
            || basic.ProcessEnvironmentBlock == 0)
        {
            return null;
        }

        if (ReadStruct<nint>(handle, basic.ProcessEnvironmentBlock + ProcessParametersOffset) is not { } parameters
            || parameters == 0)
        {
            return null;
        }

        if (ReadStruct<UnicodeString>(handle, parameters + CurrentDirectoryOffset) is not { } path
            || path.Buffer == 0
            || path.Length == 0
            || path.Length > MaximumPathBytes
            || path.Length % 2 != 0)
        {
            return null;
        }

        var text = Marshal.AllocHGlobal(path.Length);

        try
        {
            if (!ReadProcessMemory(handle, path.Buffer, text, path.Length, out _))
            {
                return null;
            }

            var value = Marshal.PtrToStringUni(text, path.Length / 2);

            // A working directory is always a rooted path. Anything else is a misread of a process
            // whose layout differs, and reporting it would put a wrong answer beside right ones.
            return value is not null && Path.IsPathRooted(value) ? value : null;
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private static T? ReadStruct<T>(nint handle, nint address) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            return ReadProcessMemory(handle, address, buffer, size, out _)
                ? Marshal.PtrToStructure<T>(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
    private static partial bool ReadProcessMemory(nint process, nint address, nint buffer, nint size, out nint read);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, nint buffer, ref uint size);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        nint process,
        int informationClass,
        out ProcessBasicInformation information,
        int length,
        out int returned);
}
