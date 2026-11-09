namespace Deguffer.Core.Health;

/// <summary>
/// One piece of device work at a time. A device call cannot be interrupted, so a caller that gives
/// up on it leaves a thread behind, and a second run of the same work would leave another. A caller
/// that arrives while the work is running joins it instead.
/// </summary>
internal sealed class SingleRun<T>
{
    private readonly Lock _gate = new();
    private Task<T>? _running;

    /// <summary>The run in progress, or a new one of <paramref name="work"/> where none is.</summary>
    internal Task<T> Join(Func<Task<T>> work)
    {
        lock (_gate)
        {
            if (_running is { } running)
            {
                return running;
            }

            var task = Task.Run(work);
            _running = task;

            task.ContinueWith(
                _ =>
                {
                    lock (_gate)
                    {
                        _running = null;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return task;
        }
    }
}
