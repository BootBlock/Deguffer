using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// A Windows job object a started tool is placed in, which is how the whole tree a tool starts is
/// reached: a <c>.cmd</c> shim's direct child is <c>cmd.exe</c>, and the real tool is beneath it.
///
/// <para>A process is placed in its jobs as it is created rather than after, by
/// <see cref="ToolProcess.Start"/>. Assigned once it was running, a tool that started a child of its
/// own in that moment would leave the child outside every job.</para>
/// </summary>
internal sealed class ToolJob : IDisposable
{
    private static readonly Lazy<ToolJob> SharedLifetime = new(() => Create(killOnClose: true));

    private readonly SafeJobHandle _handle;

    private ToolJob(SafeJobHandle handle) => _handle = handle;

    /// <summary>
    /// The job every tool Deguffer may end is placed in, which ends whatever is still in it when it
    /// closes. Never disposed: Windows closes it as Deguffer exits, however Deguffer exits, so nothing
    /// a tool started outlives Deguffer by accident.
    /// </summary>
    /// <exception cref="Win32Exception">Windows would not make the job.</exception>
    public static ToolJob Lifetime => SharedLifetime.Value;

    /// <summary>The handle <see cref="ToolProcess.Start"/> places a new process in this job through.</summary>
    internal SafeJobHandle Handle => _handle;

    /// <param name="killOnClose">
    /// Whether closing the job ends what is still in it. False for a job that is closed as soon as its
    /// one run is over: a tool can leave a process running on purpose, and that one ends with
    /// <see cref="Lifetime"/> instead.
    /// </param>
    /// <exception cref="Win32Exception">Windows would not make the job.</exception>
    public static ToolJob Create(bool killOnClose)
    {
        var handle = ToolProcessNative.CreateJobObject(0, 0);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        if (killOnClose)
        {
            var limits = new ToolProcessNative.ExtendedLimitInformation
            {
                BasicLimitInformation = { LimitFlags = ToolProcessNative.JobObjectLimitKillOnJobClose },
            };

            if (!ToolProcessNative.SetInformationJobObject(
                    handle,
                    ToolProcessNative.JobObjectExtendedLimitInformation,
                    limits,
                    Marshal.SizeOf<ToolProcessNative.ExtendedLimitInformation>()))
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error);
            }
        }

        return new ToolJob(handle);
    }

    /// <summary>
    /// Ends every process in the job, including those in any job a tool made inside it. Returns
    /// before they have exited: wait on the tool's own process for that.
    /// </summary>
    public void Terminate()
    {
        // An access-denied failure is the job's last process already gone, and there is nothing else
        // a caller that wants it ended could do differently.
        ToolProcessNative.TerminateJobObject(_handle, exitCode: 1);
    }

    public void Dispose() => _handle.Dispose();
}
