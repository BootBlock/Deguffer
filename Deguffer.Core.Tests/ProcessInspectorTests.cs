using System.ComponentModel;
using System.Diagnostics;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The process-id probe, against real processes rather than a fake.
///
/// <para>Everything the probe answers is a claim about how Windows reports a process id: which
/// refusal means the id is free and which means it is merely private, and what an exited process
/// still held open looks like. None of that exists in a fake. Each was measured before the probe was
/// written, and these tests hold the measurements in place.</para>
///
/// <para>Nothing here needs elevation or any particular tool. The processes asked about are this
/// test run, System, and a ping started for the purpose.</para>
/// </summary>
public sealed class ProcessInspectorTests
{
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
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("120");
        start.ArgumentList.Add("127.0.0.1");

        using var child = Process.Start(start)!;
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
    /// <para>System, at id 4, refuses an unelevated open. It stands in for the 143 of 378 processes
    /// that refused one in the measurement, and reading that refusal as a free id is the mistake that
    /// deletes: every service and every other account's process would then look like one that had
    /// ended.</para>
    ///
    /// <para>Elevated, System may open. The state is then still running, but a creation time can be
    /// read, so the second assertion only holds for an unelevated run.</para>
    /// </summary>
    [Fact]
    public void AProcessThisAccountMayNotOpenIsRunningWithNoCreationTime()
    {
        var liveness = new ProcessInspector().Probe(4);

        Assert.Equal(ProcessState.Running, liveness.State);

        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Null(liveness.StartedAt);
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
}
