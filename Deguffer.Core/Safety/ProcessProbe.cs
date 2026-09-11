using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// Asks Windows about one process id, at the moment of asking.
///
/// <para><b>A probe rather than a lookup in a process-table snapshot, for three reasons.</b></para>
///
/// <list type="bullet">
/// <item><b>Neither snapshot this codebase takes can say "not running".</b>
/// <see cref="RunningProcessTable"/> leaves out every process it cannot open, and
/// <see cref="ProcessInspector"/>'s snapshot keeps names without ids. An id missing from either is not
/// evidence that nothing holds it, and reading it that way is the direction that deletes.</item>
/// <item><b>Any snapshot's "not running" goes stale in that same direction.</b> It is read once per
/// planning pass, and a pass can spend minutes scanning. An id that was free when the table was read
/// can go to a new process before a provider asks about a file naming it — a session started
/// mid-scan, whose own files would then read as orphaned.</item>
/// <item><b>A snapshot would not spare the open.</b> <see cref="System.Diagnostics.Process"/> keeps no
/// creation time from its snapshot: <c>StartTime</c> opens the process to read one, and is refused
/// wherever the open is. The kernel's own list does record a creation time for every process, in
/// <c>NtQuerySystemInformation</c>'s <c>SYSTEM_PROCESS_INFORMATION</c>, but at an offset
/// <c>winternl.h</c> declares only as reserved bytes, in a structure Microsoft documents as subject to
/// change. The only answers it would improve are for processes this account may not open, and those
/// already err toward refusing.</item>
/// </list>
///
/// <para><b>What each Win32 answer means</b> was measured unelevated on one workstation rather than
/// taken from the documentation: an open of each of its 378 processes, of ids no process held, and of
/// the ids of 23 pings, each asked again after it had been killed and its
/// <see cref="System.Diagnostics.Process"/> disposed.</para>
///
/// <list type="bullet">
/// <item><c>ERROR_INVALID_PARAMETER</c>: no process holds the id. Two of the 23 released ids answered
/// it, one 13 milliseconds after release and one after two seconds. So did the idle process at id 0,
/// and no other running process — which is why an id below 1 is refused as an argument rather than
/// reported as not running.</item>
/// <item><c>ERROR_ACCESS_DENIED</c>: a process holds the id and this account may not open it. That was
/// 143 of the 378 — services, protected processes, other accounts. The id is taken, so the answer is
/// running, with no creation time. It may be a process that has exited while something still holds
/// it open, and reading that as running refuses rather than deletes.</item>
/// <item>Opened: whether it has exited is asked by waiting on it for no time at all. The exit code
/// cannot answer this, since a process may exit with <c>STILL_ACTIVE</c>'s own value, 259.
/// <b>An exited process still held open keeps its id, and that is the ordinary aftermath of an exit
/// rather than an edge case.</b> The other 21 released ids all stayed openable: 14 for the whole of a
/// five-second watch, and 7 for a 150-second one and still minutes later, by which time the process
/// that had started them had exited too and <c>Process.GetProcesses</c> no longer listed them. What
/// held them was not identified. A successful open is therefore not evidence of a running
/// process.</item>
/// </list>
///
/// <para><c>SYNCHRONIZE</c> is asked for alongside the query right because the wait needs it. One
/// process in the measurement, <c>audiodg</c>, granted the query and refused the pair. It reads as
/// running with no creation time, which errs toward refusing.</para>
///
/// <para>Windows looks an id up with its low two bits ignored: ids 5, 6 and 7 answered exactly as
/// System at id 4 did, and ids 1 to 3 as a free id. Nothing here relies on that, because it is not
/// documented. Its only effect is that an id no process can hold answers for the id beside it: as
/// running where a process holds that one, which errs toward refusing, and as free otherwise, which
/// is true.</para>
/// </summary>
internal static partial class ProcessProbe
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x0010_0000;

    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;

    public static ProcessLiveness Of(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, (uint)processId);

        if (handle == 0)
        {
            return Marshal.GetLastPInvokeError() switch
            {
                ErrorInvalidParameter => ProcessLiveness.NotRunning,
                ErrorAccessDenied => ProcessLiveness.Running(startedAt: null),

                // No other refusal appeared in the measurement. Whatever one means, it is not
                // evidence that the id is free.
                _ => ProcessLiveness.Undetermined,
            };
        }

        try
        {
            return WaitForSingleObject(handle, 0) switch
            {
                WaitTimeout => ProcessLiveness.Running(CreationTimeOf(handle)),
                WaitObject0 => ProcessLiveness.NotRunning,
                _ => ProcessLiveness.Undetermined,
            };
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static DateTimeOffset? CreationTimeOf(nint handle) =>
        GetProcessTimes(handle, out var created, out _, out _, out _)
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(created))
            : null;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
}
