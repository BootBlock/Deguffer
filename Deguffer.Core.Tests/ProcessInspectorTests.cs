using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// The process-id probe, against real processes rather than a fake.
///
/// <para>Everything the probe answers is a claim about how Windows reports a process id: which
/// refusal means the id is free and which means it is merely private, and what an exited process
/// still held open looks like. None of that exists in a fake, so each branch of the probe is pinned
/// here against a process in exactly that state.</para>
///
/// <para>Nothing here needs elevation or any particular tool. The processes asked about are this
/// test run and pings started for the purpose.</para>
/// </summary>
public sealed class ProcessInspectorTests
{
    private const uint DaclSecurityInformation = 0x4;

    /// <summary>
    /// The creation time is the one Windows recorded, to the tick, and in the FILETIME form a session
    /// registry stores it in. A time off by one tick would read every live session as a recycled id.
    /// </summary>
    [Fact]
    public void ThisProcessIsRunningAndReportsTheCreationTimeWindowsRecorded()
    {
        using var self = Process.GetCurrentProcess();

        var liveness = new ProcessInspector().Probe(Environment.ProcessId);

        Assert.Equal(ProcessState.Running, liveness.State);
        Assert.NotNull(liveness.StartedAt);
        Assert.Equal(self.StartTime.ToFileTimeUtc(), liveness.StartedAt.Value.ToFileTime());
    }

    /// <summary>
    /// A process that has exited is not running, even while something still holds it open and so
    /// keeps its id taken.
    ///
    /// <para>The <see cref="Process"/> object that started the ping is that holder here: it is not
    /// disposed until the end of the test, so the id cannot be released or reused in between. That
    /// is what makes this the discriminating case. A probe that took a successful open as proof of
    /// life passes every other test in this class and fails this one.</para>
    /// </summary>
    [Fact]
    public void AProcessThatHasExitedIsNotRunningWhileItsIdIsStillHeld()
    {
        using var child = StartPing();
        var inspector = new ProcessInspector();

        try
        {
            Assert.Equal(ProcessState.Running, inspector.Probe(child.Id).State);
        }
        finally
        {
            Stop(child);
        }

        Assert.Equal(ProcessLiveness.NotRunning, inspector.Probe(child.Id));
    }

    /// <summary>
    /// An id no process holds is not running.
    ///
    /// <para>The highest multiple of four an <see cref="int"/> can hold. Windows hands ids out from
    /// the bottom of the range, and no machine runs the half-billion processes it would take to reach
    /// this one.</para>
    /// </summary>
    [Fact]
    public void AnIdNoProcessHoldsIsNotRunning() =>
        Assert.Equal(ProcessLiveness.NotRunning, new ProcessInspector().Probe(int.MaxValue & ~3));

    /// <summary>
    /// A process this account may not open is running, not "not running".
    ///
    /// <para>That refusal was the answer for 143 of the 378 processes in the measurement: services,
    /// protected processes, other accounts'. Reading it as a free id is the mistake that deletes,
    /// because every one of them would then look like a process that had ended.</para>
    ///
    /// <para><b>The refusal belongs to the fixture, not to the account running the suite.</b> The
    /// ping is given a DACL with no entries, and Windows grants nothing to anybody through one. A
    /// system process would not do: which of them refuse depends on elevation, and an elevated run
    /// able to open one would skip the branch this test exists for while still passing. The one
    /// caller an empty DACL does not stop is one with <c>SeDebugPrivilege</c> enabled, and nothing
    /// in this suite enables it.</para>
    /// </summary>
    [Fact]
    public void AProcessThisAccountMayNotOpenIsRunningWithNoCreationTime()
    {
        using var child = StartPing();

        try
        {
            DenyEveryone(child);

            Assert.Equal(ProcessLiveness.Running(startedAt: null), new ProcessInspector().Probe(child.Id));
        }
        finally
        {
            // The handle Process.Start holds was granted before the DACL changed, so it can still
            // stop the child.
            Stop(child);
        }
    }

    /// <summary>
    /// An id below 1 is refused. The idle process at id 0 answers exactly as a free id does, so it
    /// would otherwise read as a process that is not running, and no file records either as its own.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void AnIdBelowOneIsRefusedRatherThanReportedAsNotRunning(int processId) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessInspector().Probe(processId));

    /// <summary>A ping that waits for two minutes, started without a console.</summary>
    private static Process StartPing()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("120");
        start.ArgumentList.Add("127.0.0.1");

        return Process.Start(start)!;
    }

    /// <summary>Replaces the DACL of <paramref name="process"/> with one that has no entries.</summary>
    private static void DenyEveryone(Process process)
    {
        var descriptor = new RawSecurityDescriptor("D:P");
        var binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);

        Assert.True(
            SetKernelObjectSecurity(process.SafeHandle, DaclSecurityInformation, binary),
            $"SetKernelObjectSecurity failed with error {Marshal.GetLastWin32Error()}.");
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone. Nothing to stop.
        }
    }

    // DllImport rather than LibraryImport, which needs AllowUnsafeBlocks; the test project does not
    // enable it and one fixture helper is a poor reason to.
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeProcessHandle handle, uint information, byte[] descriptor);
}
