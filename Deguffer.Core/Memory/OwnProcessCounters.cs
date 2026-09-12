using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Memory;

/// <summary>
/// Reads Deguffer's own creation time and private working set through calls Windows documents, for
/// <see cref="ProcessFigureCheck"/> to hold the process table against.
///
/// <para><b>The private working set has two documented sources, and the newer is tried first.</b>
/// <c>PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize</c> needs Windows 10 or 11 22H2 with the
/// September 2023 update, while Deguffer runs on Windows 10 1809. What an older Windows does when
/// handed the larger structure is not documented and was not tried: it may refuse it, or fill only the
/// fields it knows. Both read as no answer here, a failed call and a zero alike, so either reaches the
/// fallback. <see cref="OwnPrivateWorkingSet.Choose"/> is that decision.</para>
///
/// <para>The fallback walks this process's own working set with <c>QueryWorkingSet</c>, available on
/// every supported Windows, and counts the pages Windows does not mark shareable. Measured against the
/// newer counter on one machine, it agreed to within a tenth of a percent. It costs a pointer per page
/// of this process, which is why it is the fallback rather than the first choice.</para>
/// </summary>
internal static partial class OwnProcessCounters
{
    private const int ErrorBadLength = 24;
    private const int FallbackAttempts = 4;

    public static OwnProcessReference Read()
    {
        // The pseudo-handle for this process, which needs no closing.
        var process = GetCurrentProcess();

        if (!GetProcessTimes(process, out var created, out _, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var size = (uint)Marshal.SizeOf<MemoryCounters>();
        var counters = new MemoryCounters { Size = size };
        var answered = GetProcessMemoryInfo(process, ref counters, size);

        return new OwnProcessReference(
            created,
            OwnPrivateWorkingSet.Choose(answered, counters.PrivateWorkingSetSize, () => WalkedPrivateWorkingSet(process)));
    }

    private static long? WalkedPrivateWorkingSet(nint process)
    {
        var entries = 1 << 16;

        for (var attempt = 0; attempt < FallbackAttempts; attempt++)
        {
            // The first element is the entry count, and the entries follow it.
            var information = new nuint[entries + 1];

            if (QueryWorkingSet(process, information, information.Length * IntPtr.Size))
            {
                return OwnPrivateWorkingSet.PrivateBytes(information, Environment.SystemPageSize);
            }

            if (Marshal.GetLastPInvokeError() != ErrorBadLength)
            {
                return null;
            }

            // Too small, and the count it needed is in the first element. The working set can grow
            // before the next call, so there is room for a quarter more.
            entries = checked((int)information[0] + ((int)information[0] / 4));
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size;
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
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryWorkingSet(nint process, [Out] nuint[] information, int size);
}
