namespace Deguffer.Core.Safety;

/// <summary>
/// One task shared by everyone who asks while it runs, handed out under its owner's lock and awaited
/// by each asker outside it.
///
/// <para><b>The lock only hands the task out.</b> An asker that waited under it would hold every other
/// asker, and whatever else the owner guards with it, for as long as the task runs.</para>
///
/// <para><b>A task that failed or was stopped is not kept</b>, so the next asker starts it again rather
/// than being handed the same failure. One stopped by the token of whoever started it, and not by this
/// asker's, is started again for this asker, which would otherwise be told it was cancelled when it
/// was not.</para>
/// </summary>
internal static class SharedTask
{
    /// <param name="take">
    /// Under the owner's lock, the task held, started where none is, and what the owner keeps beside it
    /// from the same moment.
    /// </param>
    /// <param name="forget">
    /// Under the owner's lock, drops a task that failed or was stopped, where it is still the one held.
    /// </param>
    public static async Task<(T Answer, TBeside Beside)> AwaitAsync<T, TBeside>(
        Func<(Task<T> Task, TBeside Beside)> take,
        Action<Task<T>> forget,
        CancellationToken ct)
    {
        while (true)
        {
            var (task, beside) = take();

            try
            {
                return (await task.WaitAsync(ct).ConfigureAwait(false), beside);
            }
            catch (OperationCanceledException) when (task.IsCanceled && !ct.IsCancellationRequested)
            {
                // Stopped for whoever started it. This asker still wants an answer.
            }
            finally
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    forget(task);
                }
            }
        }
    }
}
