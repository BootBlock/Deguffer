using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    /// How often <see cref="EndAsync"/> asks whether the job is empty. Windows ends the processes in
    /// milliseconds, so the wait is a few of these.
    /// </summary>
    private static readonly TimeSpan EmptyPoll = TimeSpan.FromMilliseconds(10);

    /// <summary>Room for the process identifiers of a job, before it is asked again with more.</summary>
    private const int FirstListLength = 32;

    /// <summary>
    /// Ends every process in the job, including those in any job a tool made inside it, and completes
    /// once each has exited.
    ///
    /// <para><b>Exited, not merely ended.</b> Windows stops counting a process in its job a moment
    /// before the process has exited and let go of what it held, so the job going empty is not the
    /// end. Each process is held open before the job is ended, and its exit awaited. One started in
    /// the moment between is ended all the same, and waited for by the job going empty.</para>
    /// </summary>
    /// <exception cref="Win32Exception">
    /// Windows would not end them, or would not say what the job holds. Thrown rather than passed
    /// over, because a caller that went on to wait would wait for as long as the tool chose to run.
    /// </exception>
    public async Task EndAsync()
    {
        var members = Members();

        try
        {
            if (!ToolProcessNative.TerminateJobObject(_handle, exitCode: 1))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            foreach (var member in members)
            {
                await ProcessExit.Of(member).ConfigureAwait(false);
            }

            while (ActiveProcesses() > 0)
            {
                await Task.Delay(EmptyPoll).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var member in members)
            {
                member.Dispose();
            }
        }
    }

    /// <summary>
    /// A handle to each process now in the job, each confirmed to be in it once opened: an identifier
    /// listed a moment ago can already belong to a process that has nothing to do with the job.
    /// </summary>
    private List<SafeProcessHandle> Members()
    {
        var members = new List<SafeProcessHandle>();

        foreach (var processId in ProcessIds())
        {
            var process = ToolProcessNative.OpenProcess(
                ToolProcessNative.Synchronize | ToolProcessNative.ProcessQueryLimitedInformation, inheritHandle: false, processId);

            // An invalid handle is a process that exited after it was listed: nothing to wait for.
            if (!process.IsInvalid && ToolProcessNative.IsProcessInJob(process, _handle, out var inJob) && inJob)
            {
                members.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return members;
    }

    /// <summary>The identifiers of every process in the job, asked again with more room until all fit.</summary>
    private uint[] ProcessIds()
    {
        for (var length = FirstListLength; ; length *= 2)
        {
            // Two counts, then one pointer-sized identifier per process.
            var size = (2 * sizeof(uint)) + (length * nint.Size);
            var list = Marshal.AllocHGlobal(size);

            try
            {
                if (!ToolProcessNative.QueryJobProcessIds(_handle, ToolProcessNative.JobObjectBasicProcessIdList, list, size, 0)
                    && Marshal.GetLastPInvokeError() is var error and not ToolProcessNative.ErrorMoreData)
                {
                    throw new Win32Exception(error);
                }

                var assigned = (uint)Marshal.ReadInt32(list, 0);
                var listed = (uint)Marshal.ReadInt32(list, sizeof(uint));

                if (assigned > listed)
                {
                    continue;
                }

                var ids = new uint[listed];

                for (var i = 0; i < ids.Length; i++)
                {
                    ids[i] = (uint)Marshal.ReadIntPtr(list, (2 * sizeof(uint)) + (i * nint.Size));
                }

                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(list);
            }
        }
    }

    private uint ActiveProcesses()
    {
        if (!ToolProcessNative.QueryInformationJobObject(
                _handle,
                ToolProcessNative.JobObjectBasicAccountingInformation,
                out var accounting,
                Marshal.SizeOf<ToolProcessNative.BasicAccountingInformation>(),
                0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return accounting.ActiveProcesses;
    }

    public void Dispose() => _handle.Dispose();
}
