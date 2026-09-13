using System.Runtime.InteropServices;

namespace Deguffer.Core.Memory;

/// <summary>
/// Whether Windows has frozen one process, which is what §7.2.1's refusal of a suspended packaged
/// application rests on. Windows documents the flag as "the process is frozen; for example, a debugger
/// is attached and broken into the process or a Store process is suspended by a lifetime management
/// service"
/// (<see href="https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/ne-processsnapshot-pss_process_flags">PSS_PROCESS_FLAGS</see>).
///
/// <para><b>This route was chosen by measurement, as §7.2.1 requires.</b> Unelevated on one
/// workstation on 2026-09-13, a snapshot that captures nothing answered for all 285 processes a
/// <c>PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE</c> handle could be had for, 30 of them packaged.
/// The alternative, <c>IPackageDebugSettings::GetPackageExecutionState</c>, also answered unelevated
/// for all 30, but it answers about a <em>package</em> rather than about a process: it reported
/// "unknown" for 14 of the 30, including one process this call reported frozen, and it reported a
/// package suspended while one of its two processes was not frozen. It also needs an interface whose
/// other methods suspend and terminate applications, and §7.2.1 has one verb. So the per-process call
/// is the one relied on, and the per-package one is not declared at all.</para>
///
/// <para>The documentation states no access right for capturing nothing, so what an unelevated
/// Deguffer may ask was measured rather than assumed — the same footing as
/// <see cref="Safety.ProcessProbe"/>. A capture that fails answers null, which §7.2.1 refuses on.</para>
///
/// <para>Declared with <c>DllImport</c> rather than the <c>LibraryImport</c> most of Core uses, for
/// the reason <see cref="Safety.RestartManager"/> gives: the generator cannot express a structure
/// carrying a fixed-length inline buffer, and this one ends with the image name. The size of that
/// structure is the length the query demands, exactly: a buffer of any other length is refused with
/// <c>ERROR_BAD_LENGTH</c>, which reads here as a state that could not be read.</para>
/// </summary>
internal static class ProcessSnapshot
{
    private const uint Success = 0;
    private const uint CaptureNothing = 0;
    private const int QueryProcessInformation = 0;
    private const uint Frozen = 0x10;
    private const int MaxPath = 260;

    private static readonly int InformationLength = Marshal.SizeOf<SnapshotInformation>();

    public static bool? IsFrozen(nint process)
    {
        if (PssCaptureSnapshot(process, CaptureNothing, 0, out var snapshot) != Success)
        {
            return null;
        }

        try
        {
            return PssQuerySnapshot(snapshot, QueryProcessInformation, out var information, InformationLength) == Success
                ? (information.Flags & Frozen) != 0
                : null;
        }
        finally
        {
            // A snapshot of another process is still freed through this process's own handle, as
            // PssFreeSnapshot documents for a local snapshot.
            PssFreeSnapshot(GetCurrentProcess(), snapshot);
        }
    }

    /// <summary>
    /// <c>PSS_PROCESS_INFORMATION</c>, declared in full although one flag is read, because its length
    /// is what the query is asked for. Each <c>FILETIME</c> is its two halves, so that every offset
    /// after one is where the header puts it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SnapshotInformation
    {
        public uint ExitStatus;
        public nint PebBaseAddress;
        public nuint AffinityMask;
        public int BasePriority;
        public uint ProcessId;
        public uint ParentProcessId;
        public uint Flags;
        public uint CreateTimeLow;
        public uint CreateTimeHigh;
        public uint ExitTimeLow;
        public uint ExitTimeHigh;
        public uint KernelTimeLow;
        public uint KernelTimeHigh;
        public uint UserTimeLow;
        public uint UserTimeHigh;
        public uint PriorityClass;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public uint ExecuteFlags;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxPath, ArraySubType = UnmanagedType.U2)]
        public ushort[] ImageFileName;
    }

    [DllImport("kernel32.dll")]
    private static extern uint PssCaptureSnapshot(nint process, uint captureFlags, uint threadContextFlags, out nint snapshot);

    [DllImport("kernel32.dll")]
    private static extern uint PssQuerySnapshot(nint snapshot, int informationClass, out SnapshotInformation information, int length);

    [DllImport("kernel32.dll")]
    private static extern uint PssFreeSnapshot(nint process, nint snapshot);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
