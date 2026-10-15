using System.Diagnostics;

namespace Deguffer.Core.Scanning;

/// <summary>
/// What one worker does with each directory it takes, built once for that worker so whatever it reuses
/// lasts for every directory the worker reads.
/// </summary>
/// <param name="descend">How the worker stacks a child: its path, and the value carried down to it.</param>
internal delegate Action<string, TState> NewDirectoryReader<TState>(Action<string, TState> descend);

/// <summary>
/// One run of a bounded walk: a stack of directories for each worker, and up to a bound of workers
/// that each take a directory, read it, stack the children its reader chose and take the next.
///
/// <para><b>Scheduling only.</b> How a directory is listed, and which of its children are entered, is
/// the reader's. <see cref="BoundedFileWalk"/> lists the disk and never enters a reparse point, and
/// <see cref="Cloud.PlaceholderWalk"/> lists a sync root through the Cloud Files API, which tells a
/// placeholder folder from a link.</para>
///
/// <para><b>Depth-first, each worker in a subtree of its own.</b> A worker takes from the top of its
/// own <see cref="WorkStealingStack{T}"/>, so it reads a directory's children before anything stacked
/// ahead of them, the first child listed first. One that has run out steals the oldest directory
/// from the bottom of another's, the shallowest waiting, so workers spread across the top of the tree
/// and each then descends a branch of its own. One shared first-in, first-out queue made the walk
/// breadth-first: it grew to the widest level of the tree, about 145 thousand paths on a whole system
/// volume, each waiting long enough for the rest of its level to be promoted out of the young
/// generations. Depth-first, what waits is one level's siblings for each level of a branch.</para>
///
/// <para>No caller depends on the order: each merges what its workers found in a way that ignores
/// it. Explore's mid-walk pictures do show it, as a branch filling in to its full size while the
/// folders beside it are still empty. They are drawn in name order, so each fills where it already
/// is.</para>
///
/// <para><b>No worker waits for another.</b> The walk once listed a whole level and waited for all of
/// it before starting the next, so one large or slow directory kept every other worker idle, and a
/// deep, narrow tree ran mostly on one thread. Here the walk is over when no directory is waiting
/// and no worker is reading, which <see cref="_outstanding"/> counts: a directory is counted from
/// when it is stacked until its children have been stacked.</para>
///
/// <para><b>The calling thread is the first worker, and the only one that waits.</b> The others are
/// a <see cref="WorkerCrew"/>'s helpers, started only when a worker stacks more directories than it
/// will take itself, so a walk of one folder, or of a chain of single folders, starts none. The
/// calling thread waits for a directory to be stacked again, the walk to end or the token to be
/// cancelled. It makes no report while it waits: the totals only move when a directory is read, and
/// the worker that read it reports.</para>
///
/// <para><b>Progress is timed, and never concurrent with itself.</b> With no levels, the trigger is
/// elapsed time: the first directory read reports, and after that whichever worker finishes a
/// directory once <see cref="BoundedFileWalk.ProgressInterval"/> has passed since the last report
/// began. A report runs under a lock, so it never runs beside another, and a caller's progress closure
/// keeps its own state without a lock of its own, as it could when progress came between levels. A
/// worker only waits for that lock when a report is due, which is once an interval.</para>
///
/// <para>A directory's children are stacked after its report, not as its callback chooses them. A
/// report then describes the walk up to that directory and nothing below it, and a child cannot be
/// read, and find no report due, while its parent's report is still being made. Every report also
/// happens before its directory is counted done, so none can arrive after <see cref="Run"/> returns,
/// and none is made once the walk is stopping.</para>
/// </summary>
internal sealed class WalkWorkers<TState>
{
    private const int CallingThread = 0;

    // One for each thread the walk may use. The calling thread's is the first, and a helper borrows
    // whichever other one is free for each turn it takes, which _borrowed marks.
    private readonly WorkStealingStack<(string Path, TState State)>[] _stacks;
    private readonly int[] _borrowed;
    private readonly Lock _reporting = new();

    private readonly WorkerCrew _crew;
    private readonly Func<bool> _readyToLook;
    private readonly NewDirectoryReader<TState> _newReader;
    private readonly Action<bool> _onProgress;
    private readonly TimeProvider _clock;
    private readonly long _progressTicks;
    private readonly CancellationToken _ct;

    private int _outstanding;

    // Directories stacked and not yet taken, across every stack. Raised before a push and lowered
    // after a take, so it is never below what the stacks hold, and a worker told nothing is waiting
    // has nothing to find.
    private int _waiting;
    private long _nextReport;

    /// <param name="threads">How many directories are read at once, the calling thread included.</param>
    public WalkWorkers(
        int threads,
        NewDirectoryReader<TState> newReader,
        Action<bool> onProgress,
        TimeProvider clock,
        CancellationToken ct)
    {
        _stacks = [.. Enumerable.Range(0, threads).Select(_ => new WorkStealingStack<(string, TState)>())];
        _borrowed = new int[threads];
        _crew = new WorkerCrew(threads, NewHelper, () => !Stopping && Volatile.Read(ref _waiting) > 0);
        _readyToLook = () => Volatile.Read(ref _waiting) > 0 || Stopping || Volatile.Read(ref _outstanding) == 0;
        _newReader = newReader;
        _onProgress = onProgress;
        _clock = clock;
        _progressTicks = (long)(BoundedFileWalk.ProgressInterval.TotalSeconds * clock.TimestampFrequency);
        _ct = ct;
    }

    private bool Stopping => _crew.Failed || _ct.IsCancellationRequested;

    public void Run(string root, TState rootState) => Run([(root, rootState)]);

    /// <summary>
    /// Walk every one of <paramref name="roots"/> in the one run, so a list of small trees fills the
    /// threads that one of them alone would leave idle.
    ///
    /// <para>Helpers are started for the roots as for a directory's children, because a root holding
    /// no directory stacks nothing, and a list of those would otherwise run on the calling thread
    /// alone.</para>
    /// </summary>
    public void Run(IReadOnlyList<(string Path, TState State)> roots)
    {
        using var wake = _crew.WakeOn(_ct);

        _nextReport = _clock.GetTimestamp();

        Stack(CallingThread, [.. roots]);
        StartHelpers(roots.Count - 1);

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

        _onProgress(true);
    }

    private void WorkAndWait(Worker worker)
    {
        while (true)
        {
            worker.Drain(CallingThread);

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

        return () =>
        {
            // Borrowed for this turn only. A helper takes a turn only while the crew counts it, and is
            // counted out only after the turn, so no more turns run at once than the helpers allowed,
            // which is one fewer than the stacks.
            var slot = BorrowStack();
            try
            {
                worker.Drain(slot);
            }
            finally
            {
                Volatile.Write(ref _borrowed[slot], 0);
            }
        };
    }

    private int BorrowStack()
    {
        for (var slot = CallingThread + 1; slot < _borrowed.Length; slot++)
        {
            if (Interlocked.CompareExchange(ref _borrowed[slot], 1, 0) == 0)
            {
                return slot;
            }
        }

        throw new UnreachableException("More helpers took a turn at once than the crew allows.");
    }

    /// <summary>
    /// Push <paramref name="directories"/> onto the stack at <paramref name="slot"/>, the first of them
    /// to be taken first, and count each until its own children have been stacked.
    /// </summary>
    private void Stack(int slot, List<(string Path, TState State)> directories)
    {
        if (directories.Count == 0)
        {
            return;
        }

        Interlocked.Add(ref _outstanding, directories.Count);
        Interlocked.Add(ref _waiting, directories.Count);
        _stacks[slot].PushAll(directories);
        _crew.Signal();
    }

    /// <summary>
    /// The newest directory on the stack at <paramref name="slot"/>, or failing that the oldest on
    /// another, looking first at the stack after its own so that thieves spread across the others.
    /// </summary>
    private bool TryTake(int slot, out (string Path, TState State) next)
    {
        var found = _stacks[slot].TryPop(out next);

        for (var i = 1; !found && i < _stacks.Length; i++)
        {
            found = _stacks[(slot + i) % _stacks.Length].TrySteal(out next);
        }

        if (found)
        {
            Interlocked.Decrement(ref _waiting);
        }

        return found;
    }

    /// <summary>
    /// Start up to <paramref name="wanted"/> helpers, as far as the thread count allows. A worker that
    /// has just stacked some directories takes one of them itself, so it asks for one fewer.
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
            _onProgress(false);
        }
    }

    /// <summary>
    /// What one worker reuses for every directory it reads: its reader, and the children that reader
    /// chose, held until the directory's report has been made.
    /// </summary>
    private sealed class Worker
    {
        private readonly WalkWorkers<TState> _walk;
        private readonly List<(string Path, TState State)> _chosen = [];
        private readonly Action<string, TState> _read;

        public Worker(WalkWorkers<TState> walk)
        {
            _walk = walk;
            _read = walk._newReader((path, state) => _chosen.Add((path, state)));
        }

        /// <summary>
        /// Read directories from the stack at <paramref name="slot"/>, and from the others once it is
        /// empty, until none is left to take or the walk is stopping.
        /// </summary>
        public void Drain(int slot)
        {
            while (!_walk.Stopping && _walk.TryTake(slot, out var next))
            {
                Read(slot, next);
            }
        }

        private void Read(int slot, (string Path, TState State) next)
        {
            _chosen.Clear();
            _read(next.Path, next.State);

            _walk.ReportIfDue();

            _walk.Stack(slot, _chosen);
            _walk.StartHelpers(_chosen.Count - 1);

            if (Interlocked.Decrement(ref _walk._outstanding) == 0)
            {
                _walk._crew.Signal();
            }
        }
    }
}
