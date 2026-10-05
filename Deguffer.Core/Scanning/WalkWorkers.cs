using System.Collections.Concurrent;

namespace Deguffer.Core.Scanning;

/// <summary>
/// One run of <see cref="BoundedFileWalk"/>: a queue of directories, and up to
/// <see cref="WalkTuning.Threads"/> workers that each take a directory, list it, queue the
/// subdirectories its caller chose and take the next.
///
/// <para><b>No worker waits for another.</b> The walk once listed a whole level and waited for all of
/// it before starting the next, so one large or slow directory kept every other worker idle, and a
/// deep, narrow tree ran mostly on one thread. Here the walk is over when the queue is empty and no
/// worker is reading, which <see cref="_outstanding"/> counts: a directory is counted from when it is
/// queued until its children have been queued.</para>
///
/// <para><b>The calling thread is the first worker, and the only one that waits.</b> The others are
/// a <see cref="WorkerCrew"/>'s helpers, started only when a worker queues more directories than it
/// will take itself, so a walk of one folder, or of a chain of single folders, starts none. The
/// calling thread waits for the queue to fill again, the walk to end or the token to be cancelled.
/// It makes no report while it waits: the totals only move when a directory is read, and the worker
/// that read it reports.</para>
///
/// <para><b>Progress is timed, and never concurrent with itself.</b> With no levels, the trigger is
/// elapsed time: the first directory read reports, and after that whichever worker finishes a
/// directory once <see cref="BoundedFileWalk.ProgressInterval"/> has passed since the last report
/// began. A report runs under a lock, so it never runs beside another, and a caller's progress closure
/// keeps its own state without a lock of its own, as it could when progress came between levels. A
/// worker only waits for that lock when a report is due, which is once an interval.</para>
///
/// <para>A directory's children are queued after its report, not as its callback chooses them. A
/// report then describes the walk up to that directory and nothing below it, and a child cannot be
/// read, and find no report due, while its parent's report is still being made. Every report also
/// happens before its directory is counted done, so none can arrive after <see cref="Run"/> returns,
/// and none is made once the walk is stopping.</para>
/// </summary>
internal sealed class WalkWorkers<TState>
{
    private readonly ConcurrentQueue<(string Path, TState State)> _pending = new();
    private readonly Lock _reporting = new();

    private readonly WorkerCrew _crew;
    private readonly Func<bool> _readyToLook;
    private readonly ListingBuffer _buffer;
    private readonly Action<TState, DirectoryContents, Action<WalkEntry, TState>> _onDirectory;
    private readonly Action _onProgress;
    private readonly TimeProvider _clock;
    private readonly long _progressTicks;
    private readonly CancellationToken _ct;

    private int _outstanding;
    private long _nextReport;

    public WalkWorkers(
        WalkTuning tuning,
        Action<TState, DirectoryContents, Action<WalkEntry, TState>> onDirectory,
        Action onProgress,
        TimeProvider clock,
        CancellationToken ct)
    {
        _crew = new WorkerCrew(tuning.Threads, NewHelper, () => !Stopping && !_pending.IsEmpty);
        _readyToLook = () => !_pending.IsEmpty || Stopping || Volatile.Read(ref _outstanding) == 0;
        _buffer = new ListingBuffer(tuning.ListingBufferBytes);
        _onDirectory = onDirectory;
        _onProgress = onProgress;
        _clock = clock;
        _progressTicks = (long)(BoundedFileWalk.ProgressInterval.TotalSeconds * clock.TimestampFrequency);
        _ct = ct;
    }

    private bool Stopping => _crew.Failed || _ct.IsCancellationRequested;

    /// <param name="root">In the extended form (§6.3).</param>
    public void Run(string root, TState rootState)
    {
        using var wake = _crew.WakeOn(_ct);

        _nextReport = _clock.GetTimestamp();
        Enqueue(root, rootState);

        try
        {
            WorkAndWait(new Worker(this));
        }
        catch (Exception ex)
        {
            // Held, not thrown here: the helpers are still inside the caller's callbacks, and stop
            // only once they see it. It is thrown below, unchanged, when they have.
            _crew.Fail(ex);
        }

        _crew.Finish();
        _ct.ThrowIfCancellationRequested();

        _onProgress();
    }

    private void WorkAndWait(Worker worker)
    {
        while (true)
        {
            Drain(worker);

            if (Stopping || Volatile.Read(ref _outstanding) == 0)
            {
                return;
            }

            _crew.WaitUnless(_readyToLook);
        }
    }

    private Action NewHelper()
    {
        var worker = new Worker(this);
        return () => Drain(worker);
    }

    private void Drain(Worker worker)
    {
        while (!Stopping && _pending.TryDequeue(out var next))
        {
            worker.Read(next);
        }
    }

    private void Enqueue(string path, TState state)
    {
        Interlocked.Increment(ref _outstanding);
        _pending.Enqueue((path, state));
        _crew.Signal();
    }

    /// <summary>
    /// Start up to <paramref name="wanted"/> helpers, as far as the thread count allows. A worker that
    /// has just queued some directories takes one of them itself, so it asks for one fewer.
    /// </summary>
    private void StartHelpers(int wanted)
    {
        var started = 0;
        while (started < wanted && _crew.TryStartHelper())
        {
            started++;
        }
    }

    private void ReportIfDue()
    {
        if (Stopping || _clock.GetTimestamp() < Volatile.Read(ref _nextReport))
        {
            return;
        }

        lock (_reporting)
        {
            // Asked again under the lock: the worker that held it may have just reported.
            var now = _clock.GetTimestamp();
            if (Stopping || now < _nextReport)
            {
                return;
            }

            // Set before the report rather than after it, so the interval runs from when a report
            // began, and a report slower than the interval does not stretch it.
            Volatile.Write(ref _nextReport, now + _progressTicks);
            _onProgress();
        }
    }

    /// <summary>
    /// What one worker reuses for every directory it reads: its listing, and the children its caller
    /// chose, held until the directory's report has been made.
    /// </summary>
    private sealed class Worker
    {
        private readonly WalkWorkers<TState> _walk;
        private readonly WalkListing _listing;
        private readonly List<(string Path, TState State)> _chosen = [];
        private readonly Action<WalkEntry, TState> _descend;

        public Worker(WalkWorkers<TState> walk)
        {
            _walk = walk;
            _listing = new WalkListing(walk._buffer);
            _descend = Descend;
        }

        public void Read((string Path, TState State) next)
        {
            _chosen.Clear();
            _walk._onDirectory(next.State, _listing.Read(next.Path), _descend);

            _walk.ReportIfDue();

            foreach (var (path, state) in _chosen)
            {
                _walk.Enqueue(path, state);
            }

            _walk.StartHelpers(_chosen.Count - 1);

            if (Interlocked.Decrement(ref _walk._outstanding) == 0)
            {
                _walk._crew.Signal();
            }
        }

        private void Descend(WalkEntry directory, TState state)
        {
            // The walk holds this rule rather than trusting the caller to, because the caller is the
            // half that changes. A junction's target keeps its own place on the volume, so descending
            // through one both counts it twice and describes a tree nothing classified — and a link is
            // handed back rather than dropped, so a caller iterating the wrong list is a mistake that
            // can be made.
            if (!directory.IsReparsePoint)
            {
                _chosen.Add((directory.FullName, state));
            }
        }
    }
}
