using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// Asks Windows about one process id, at the moment of asking.
///
/// <para><b>A probe rather than a row of a process-table snapshot, for three reasons.</b></para>
///
/// <list type="bullet">
/// <item><b>A snapshot cannot say "not running".</b> <see cref="RunningProcessTable"/> leaves out every
/// process it cannot open, and <see cref="ProcessInspector"/>'s snapshot keeps names without ids. An
/// id missing from either is not evidence that nothing holds it, and reading it that way is the
/// direction that deletes.</item>
/// <item><b>A snapshot's "not running" goes stale in that same direction.</b> It is read once per
/// planning pass, and a pass can spend minutes scanning. An id that was free when the table was read
/// can go to a new process before a provider asks about a file naming it — a session started
/// mid-scan, whose own files would then read as orphaned.</item>
/// <item><b>A snapshot would save nothing.</b> The creation time needs a handle to the process
/// whichever way the id is found, so the probe is one open per question against a walk of every
/// process on the machine.</item>
/// </list>
///
/// <para><b>What each Win32 answer means</b> was measured unelevated against all 378 processes on one
/// workstation, rather than taken from the documentation:</para>
///
/// <list type="bullet">
/// <item><c>ERROR_INVALID_PARAMETER</c>: no process holds the id. The idle process at id 0 answers
/// the same, which is why an id below 1 is refused as an argument rather than reported as not
/// running.</item>
/// <item><c>ERROR_ACCESS_DENIED</c>: a process holds the id and this account may not open it. That was
/// 143 of the 378 — services, protected processes, other accounts. The id is taken, so the answer is
/// running, with no creation time. It may be a process that has exited while something still holds
/// it open, and reading that as running refuses rather than deletes.</item>
/// <item>Opened: whether it has exited is asked by waiting on it for no time at all. The exit code
/// cannot answer this, since a process may exit with <c>STILL_ACTIVE</c>'s own value, 259. An exited
/// process that something still holds open keeps its id — a <see cref="System.Diagnostics.Process"/>
/// object that started it is one such holder — so a successful open is not on its own evidence of a
/// running process.</item>
/// </list>
///
/// <para><c>SYNCHRONIZE</c> is asked for alongside the query right because the wait needs it. One
/// process in the measurement, <c>audiodg</c>, granted the query and refused the pair. It reads as
/// running with no creation time, which errs toward refusing.</para>
///
/// <para>Windows looks an id up with its low two bits ignored: ids 5, 6 and 7 answered exactly as
/// System at id 4 did, where id 1 answered as a free id. Nothing here relies on that, because it is
/// not documented. Its only effect is that a malformed id reads as the process beside it, which errs
/// toward running.</para>
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
