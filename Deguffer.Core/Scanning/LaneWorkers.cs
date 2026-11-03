namespace Deguffer.Core.Scanning;

/// <summary>
/// Works through a queue of items for each lane, each lane on up to its own number of threads and
/// every lane at once, where working an item may queue more in any lane. For reads that block on the
/// disk, a lane a disk, where what one read finds decides what is read next on another disk.
///
/// <para><b>No lane waits for another.</b> A lane that has emptied its queue waits for more rather
/// than ending, because an item still under way in another lane may queue work in it. The run is
/// over when no item is queued or under way in any lane, which <see cref="_outstanding"/> counts: an
/// item is counted from when it is queued until its work has returned, so the items its work queued
/// are counted before it stops being.</para>
///
/// <para><b>The highest priority first, within a lane.</b> Each lane takes the item of the highest
/// priority it holds, and of two the same, the one queued first. An item queued in a lane's middle
/// overtakes the lower ones still waiting there, whatever lane queued it.</para>
///
/// <para><b>Each lane's own bound, always.</b> A lane's calling thread and its
/// <see cref="WorkerCrew"/>'s helpers are the only threads that work its items, so a lane of one is
/// never worked by two at once however many items arrive in it from elsewhere.</para>
///
/// <para><b>Stops every lane</b> once <c>ct</c> is cancelled or any item has failed. An item already
/// under way finishes, and nothing is thrown until every thread of every lane has: a lane's failure,
/// unchanged, the first lane's where two failed at once, or else
/// <see cref="OperationCanceledException"/>.</para>
/// </summary>
internal sealed class LaneWorkers<T>
{
    private readonly Lane[] _lanes;
    private readonly Action<Action> _runLane;
    private readonly CancellationToken _ct;

    private Action<T> _work = static _ => { };
    private int _outstanding;
    private int _failed;
    private long _queued;

    /// <param name="threads">How many threads may work each lane at once, its calling thread included.</param>
    public LaneWorkers(IReadOnlyList<int> threads, CancellationToken ct)
        : this(threads, WorkerThreads.Shared.Run, ct)
    {
    }

    /// <param name="runLane">Runs a lane's driving thread: the seam a test fails a thread start through.</param>
    internal LaneWorkers(IReadOnlyList<int> threads, Action<Action> runLane, CancellationToken ct)
    {
        _lanes = [.. threads.Select(count => new Lane(this, count))];
        _runLane = runLane;
        _ct = ct;
    }

    private bool Stopping => Volatile.Read(ref _failed) != 0 || _ct.IsCancellationRequested;

    /// <summary>
    /// Queue <paramref name="item"/> in <paramref name="lane"/>, ahead of every item there of a lower
    /// <paramref name="priority"/>, and start a helper there if its bound allows one. Only from the
    /// work of an item, so that the run cannot have ended.
    /// </summary>
    public void Add(int lane, T item, long priority)
    {
        _lanes[lane].Queue(item, priority);
        _lanes[lane].Crew.TryStartHelper();
    }

    /// <summary>
    /// Queue <paramref name="first"/>, then call <paramref name="work"/> once for every item queued,
    /// those its calls queue included, and end once none is queued or under way in any lane.
    /// </summary>
    /// <exception cref="OperationCanceledException"><c>ct</c> was cancelled.</exception>
    public async Task RunAsync(IEnumerable<(int Lane, T Item, long Priority)> first, Action<T> work)
    {
        _work = work;

        foreach (var (lane, item, priority) in first)
        {
            _lanes[lane].Queue(item, priority);
        }

        if (Volatile.Read(ref _outstanding) == 0)
        {
            return;
        }

        await Task.WhenAll(_lanes.Select(RunLaneAsync)).ConfigureAwait(false);
    }

    /// <summary>
    /// Run <paramref name="lane"/> on a driving thread of its own. A lane whose thread would not start
    /// never takes its items, which stay counted, so every other lane is stopped rather than left
    /// waiting for them for ever.
    /// </summary>
    private Task RunLaneAsync(Lane lane)
    {
        var running = WorkerThreads.RunAsync(
            _runLane,
            () =>
            {
                lane.Run();
                return true;
            },
            _ct);

        running.ContinueWith(
            static (_, run) => ((LaneWorkers<T>)run!).StopEveryLane(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return running;
    }

    private void Stop(Lane failed, Exception ex)
    {
        failed.Crew.Fail(ex);
        StopEveryLane();
    }

    private void StopEveryLane()
    {
        Volatile.Write(ref _failed, 1);
        WakeEveryLane();
    }

    private void WakeEveryLane()
    {
        foreach (var lane in _lanes)
        {
            lane.Crew.Signal();
        }
    }

    /// <summary>One lane's queue, and the threads that work it.</summary>
    private sealed class Lane
    {
        private readonly LaneWorkers<T> _run;
        private readonly int _threads;
        private readonly Lock _gate = new();

        // Guarded by _gate.
        private readonly PriorityQueue<T, Order> _items = new();

        // What _items holds, readable without the lock. Raised with the queue under the lock, so it is
        // never below what the queue holds once the lock is let go.
        private int _waiting;

        // Set as the driving thread starts. No helper starts before it, because only the driving
        // thread waits for the helpers, and one started in a lane whose thread never ran would be
        // waited for by nothing.
        private int _driven;

        public Lane(LaneWorkers<T> run, int threads)
        {
            _run = run;
            _threads = threads;

            Action drain = Drain;
            Crew = new WorkerCrew(
                threads, () => drain, () => Volatile.Read(ref _driven) != 0 && !_run.Stopping && Volatile.Read(ref _waiting) > 0);
        }

        public WorkerCrew Crew { get; }

        public void Queue(T item, long priority)
        {
            // Counted before it can be taken, so the run is never seen over while it waits.
            Interlocked.Increment(ref _run._outstanding);

            lock (_gate)
            {
                _items.Enqueue(item, new Order(priority, Interlocked.Increment(ref _run._queued)));
                _waiting++;
            }

            Crew.Signal();
        }

        /// <summary>Work the lane on the calling thread, with its helpers, until the run is over or stopping.</summary>
        public void Run()
        {
            Volatile.Write(ref _driven, 1);
            using var wake = Crew.WakeOn(_run._ct);

            var started = 0;
            while (started < Math.Min(_threads, Volatile.Read(ref _waiting)) - 1 && Crew.TryStartHelper())
            {
                started++;
            }

            while (true)
            {
                Drain();

                if (_run.Stopping || Volatile.Read(ref _run._outstanding) == 0)
                {
                    break;
                }

                Crew.WaitUnless(() => Volatile.Read(ref _waiting) > 0 || _run.Stopping || Volatile.Read(ref _run._outstanding) == 0);
            }

            Crew.Finish();
            _run._ct.ThrowIfCancellationRequested();
        }

        private void Drain()
        {
            while (!_run.Stopping && TryTake(out var item))
            {
                try
                {
                    _run._work(item);
                }
                catch (Exception ex)
                {
                    // Not swallowed: held by this lane's crew, which throws it unchanged once its
                    // helpers have ended. Every other lane is stopped too, or it would wait for
                    // items this one will never finish.
                    _run.Stop(this, ex);
                    return;
                }

                if (Interlocked.Decrement(ref _run._outstanding) == 0)
                {
                    _run.WakeEveryLane();
                }
            }
        }

        private bool TryTake(out T item)
        {
            lock (_gate)
            {
                if (!_items.TryDequeue(out item!, out _))
                {
                    return false;
                }

                _waiting--;
                return true;
            }
        }
    }

    /// <summary>The higher priority first, and of two the same, the one queued first.</summary>
    private readonly record struct Order(long Priority, long Queued) : IComparable<Order>
    {
        public int CompareTo(Order other) =>
            Priority != other.Priority ? other.Priority.CompareTo(Priority) : Queued.CompareTo(other.Queued);
    }
}
