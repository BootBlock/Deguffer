namespace Deguffer.Core.Viewing;

/// <summary>
/// Hands what is reported from any thread to the thread that made it, in batches, so a search that
/// confirms thousands of results a second costs the page's thread one post for each batch rather
/// than one for each result.
///
/// <para><b>At most one batch is waiting at a time.</b> The first report after a quiet spell is
/// posted at once, so a result that arrives alone is shown without delay. Reports that arrive
/// within <see cref="Interval"/> of the last batch's start wait for the next one, and join it.</para>
///
/// <para><b>A batch takes the thread for about a frame at most</b> (<see cref="Budget"/>). Whatever
/// it leaves is posted again at once, because it has waited already, and the thread draws and takes
/// input between the two.</para>
///
/// <para><b>The end is awaited, not assumed.</b> <see cref="EndAsync"/> answers once everything
/// reported is handed over, so the code that says a search has ended runs after its last result is
/// shown.</para>
/// </summary>
/// <typeparam name="T">What is reported.</typeparam>
public sealed class BatchedProgress<T> : IProgress<T>, IDisposable
{
    /// <summary>The shortest time between the starts of two batches while reports keep arriving.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    /// <summary>How long one batch may run before it leaves the rest to the next.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(16);

    private readonly SynchronizationContext _context;
    private readonly TimeProvider _time;
    private readonly Action<T> _each;
    private readonly Action _batched;
    private readonly ITimer _timer;
    private readonly SendOrPostCallback _batch;
    private readonly Lock _gate = new();

    // Everything below is read and written under _gate.
    private readonly Queue<T> _queue = new();
    private Waiting _waiting;
    private long? _lastStarted;
    private bool _ended;
    private TaskCompletionSource? _drained;

    /// <summary>
    /// Made on the thread the batches are handed to, whose context it posts them through. On a thread
    /// with none, as <see cref="Progress{T}"/> does, each batch runs on the thread pool, one at a time.
    /// </summary>
    /// <param name="each">Given each report, in the order reported, on this thread.</param>
    /// <param name="batched">Called on this thread after each batch, for whatever is shown once per batch rather than once per report.</param>
    public BatchedProgress(TimeProvider time, Action<T> each, Action batched)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(each);
        ArgumentNullException.ThrowIfNull(batched);

        _context = SynchronizationContext.Current ?? new SynchronizationContext();
        _time = time;
        _each = each;
        _batched = batched;
        _batch = _ => Batch();
        _timer = time.CreateTimer(_ => Due(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private enum Waiting
    {
        /// <summary>No batch is waiting, so nothing is queued.</summary>
        None,

        /// <summary>The timer posts the next batch when the interval since the last one has passed.</summary>
        Timed,

        /// <summary>The next batch is posted to the thread.</summary>
        Posted,
    }

    /// <summary>Queue <paramref name="value"/> for the next batch, from any thread.</summary>
    /// <exception cref="InvalidOperationException">The reports were said to have ended (<see cref="EndAsync"/>).</exception>
    public void Report(T value)
    {
        lock (_gate)
        {
            if (_ended)
            {
                throw new InvalidOperationException("Reported after the reports were said to have ended.");
            }

            _queue.Enqueue(value);

            if (_waiting == Waiting.None)
            {
                Schedule();
            }
        }
    }

    /// <summary>
    /// Say nothing more will be reported, and answer once everything that was is handed over. What
    /// is still queued is posted at once rather than at the interval, since nothing follows it.
    /// </summary>
    public Task EndAsync()
    {
        lock (_gate)
        {
            _ended = true;

            if (_waiting == Waiting.None)
            {
                return Task.CompletedTask;
            }

            // Run apart from the batch that completes it, so the code awaiting the end runs after
            // that batch has finished, not inside it.
            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (_waiting == Waiting.Timed)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                Post();
            }

            return _drained.Task;
        }
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>Wait for the next batch, under <see cref="_gate"/>, with something queued and no batch waiting.</summary>
    private void Schedule()
    {
        var wait = _lastStarted is { } last ? Interval - _time.GetElapsedTime(last) : TimeSpan.Zero;

        if (wait > TimeSpan.Zero)
        {
            _waiting = Waiting.Timed;
            _timer.Change(wait, Timeout.InfiniteTimeSpan);
        }
        else
        {
            Post();
        }
    }

    private void Post()
    {
        _waiting = Waiting.Posted;
        _context.Post(_batch, null);
    }

    private void Due()
    {
        lock (_gate)
        {
            // The end may have posted the batch already.
            if (_waiting == Waiting.Timed)
            {
                Post();
            }
        }
    }

    private void Batch()
    {
        var started = _time.GetTimestamp();

        try
        {
            while (Next(out var value))
            {
                _each(value);

                if (_time.GetElapsedTime(started) >= Budget)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _lastStarted = started;

                if (_queue.Count > 0)
                {
                    // Left by the budget, or by a report that threw: it has waited already.
                    Post();
                }
                else
                {
                    _waiting = Waiting.None;

                    if (_ended)
                    {
                        _drained?.TrySetResult();
                    }
                }
            }

            _batched();
        }
    }

    private bool Next(out T value)
    {
        lock (_gate)
        {
            return _queue.TryDequeue(out value!);
        }
    }
}
