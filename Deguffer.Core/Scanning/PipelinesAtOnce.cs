namespace Deguffer.Core.Scanning;

/// <summary>
/// Runs the pipelines of one scan or measurement at the same time, one for each set of physical disks
/// (<see cref="FolderPipelines"/>), so work on two disks costs the longer of the two, not their sum.
///
/// <para><b>Bounded by the disks.</b> There is one pipeline for each set of disks the paths are on,
/// one for everything on the network, and one for each optical drive, so never more than the machine
/// has disks and optical drives, and one more. Each pass in one bounds its own threads by its volume's
/// tuning, as it does when it runs alone.</para>
///
/// <para><b>One failure stops the rest.</b> The work has failed once any pipeline has, and the others
/// would otherwise read whole volumes for a result nobody receives. What failed is what is thrown,
/// never the stop it caused in the others.</para>
/// </summary>
internal static class PipelinesAtOnce
{
    /// <param name="run">
    /// One pipeline's work, started on a thread of its own, as <see cref="WorkerThreads"/> says a pass
    /// must, so a pipeline that blocks before its first wait does not hold back the next one's start.
    /// It must stop at the token it is given.
    /// </param>
    public static Task RunAsync<T>(IReadOnlyList<T> pipelines, Func<T, CancellationToken, Task> run, CancellationToken ct) =>
        pipelines.Count switch
        {
            0 => Task.CompletedTask,
            1 => run(pipelines[0], ct),
            _ => RunEachAsync(pipelines, run, ct),
        };

    private static async Task RunEachAsync<T>(IReadOnlyList<T> pipelines, Func<T, CancellationToken, Task> run, CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task StopTheRestUnlessFinished(T pipeline)
        {
            var finished = false;

            try
            {
                await run(pipeline, stopping.Token).ConfigureAwait(false);
                finished = true;
            }
            finally
            {
                if (!finished)
                {
                    stopping.Cancel();
                }
            }
        }

        // A pipeline the stop reached ends cancelled, not faulted, because every pass throws for the
        // token it was given, and Task.WhenAll throws a failure in preference to a cancellation.
        await Task.WhenAll([.. pipelines.Select(StopTheRestUnlessFinished)]).ConfigureAwait(false);
    }
}
