using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>A process's exit, awaited through a handle the caller holds open until it completes.</summary>
internal static class ProcessExit
{
    /// <summary>Completes once <paramref name="process"/> has exited.</summary>
    public static async Task Of(SafeProcessHandle process)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var wait = new ProcessWait(process);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            wait, (_, _) => exited.TrySetResult(), state: null, Timeout.Infinite, executeOnlyOnce: true);

        try
        {
            await exited.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    /// <summary>The process handle, waited on without being owned.</summary>
    private sealed class ProcessWait : WaitHandle
    {
        public ProcessWait(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
