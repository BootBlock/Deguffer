namespace Deguffer.Core.Scanning;

/// <summary>
/// Works through a list by index on as many threads as the caller allows: the calling thread, and the
/// rest a <see cref="WorkerCrew"/>'s helpers, each taking the next index until none is left. For work
/// that blocks on the disk: deleting files and folders, opening a file to ask whether it can go, and
/// reading one.
///
/// <para><b>Not the thread pool.</b> Each item blocks its thread for as long as the disk takes. On the
/// pool, a list bound to four threads is worked by as many as the pool has free, and the pool starts
/// threads past its minimum, one per processor, only once it finds itself starved, about one every
/// half second. Until it does, the list runs below its bound, as the content-matching tests saw with
/// the processor count held at four, and the rest of the process's pool work waits behind it. On
/// threads of their own (<see cref="WorkerThreads"/>) the list is worked by its bound from the first
/// item. A caller on async code starts the run on
/// <see cref="WorkerThreads.RunAsync{T}(Func{T}, CancellationToken)"/>, so the calling thread is not
/// the pool's either.</para>
///
/// <para><b>Stops taking items</b> once <c>ct</c> is cancelled or an item has failed. An item already
/// under way finishes, and nothing is thrown until every thread has: the first failure, unchanged, or
/// else <see cref="OperationCanceledException"/>.</para>
/// </summary>
internal sealed class ListWorkers
{
    private readonly int _count;
    private readonly Action<int> _work;
    private readonly CancellationToken _ct;
    private readonly WorkerCrew _crew;
    private readonly int _helpersWanted;

    private int _next = -1;

    private ListWorkers(int count, int threads, Action<int> work, CancellationToken ct)
    {
        _count = count;
        _work = work;
        _ct = ct;

        // No more helpers than items to give them, the calling thread taking one of its own.
        _helpersWanted = Math.Min(threads, count) - 1;

        Action workWhileAny = WorkWhileAny;
        _crew = new WorkerCrew(threads, () => workWhileAny, () => !Stopping && Volatile.Read(ref _next) + 1 < _count);
    }

    private bool Stopping => _crew.Failed || _ct.IsCancellationRequested;

    /// <summary>
    /// Call <paramref name="work"/> once for each index from 0 to <paramref name="count"/>, on this
    /// thread and up to <paramref name="threads"/> in all, and return once every thread has ended.
    /// </summary>
    /// <param name="threads">How many threads may work at once, the calling thread included.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static void Run(int count, int threads, Action<int> work, CancellationToken ct) =>
        new ListWorkers(count, threads, work, ct).Run();

    private void Run()
    {
        var started = 0;
        while (started < _helpersWanted && _crew.TryStartHelper())
        {
            started++;
        }

        try
        {
            WorkWhileAny();
        }
        catch (Exception ex)
        {
            // Held, not thrown here: the helpers are still inside the caller's work, and stop only once
            // they see it. It is thrown below, unchanged, when they have.
            _crew.Fail(ex);
        }

        _crew.Finish();
        _ct.ThrowIfCancellationRequested();
    }

    private void WorkWhileAny()
    {
        while (!Stopping)
        {
            var item = Interlocked.Increment(ref _next);
            if (item >= _count)
            {
                return;
            }

            _work(item);
        }
    }
}
