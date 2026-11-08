using System.Runtime.ExceptionServices;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The threads of one parallel run: the calling thread, which works and is the only one that waits,
/// and up to a bound of helpers on <see cref="WorkerThreads"/>, each started when there is work for
/// it and ended when there is none. Async code starts the run on
/// <see cref="WorkerThreads.RunAsync{T}(Func{T}, CancellationToken)"/>, so the calling thread is not
/// the pool's either.
///
/// <para>The walk (<see cref="WalkWorkers{TState}"/>), the read of the file table
/// (<see cref="Mft.MftReadPass"/>), the work through a list of files that blocks on the disk
/// (<see cref="ListWorkers"/>), the reads of a duplicate search, a crew a disk
/// (<see cref="LaneWorkers{T}"/>), and a removal's deletions beside its walk
/// (<see cref="Execution.TreeRemoval"/>) differ in what their work is and in when they are over. They
/// agree in how a helper starts, ends, fails and is waited for, which is what this holds.</para>
///
/// <para><b>A helper that finds no work ends</b> rather than holding a thread. The calling
/// thread instead waits for a <see cref="Signal"/>, which anything that changes what it is waiting
/// on has to give.</para>
/// </summary>
internal sealed class WorkerCrew
{
    // Never disposed. A helper sets it after the count that lets the calling thread return, so it can
    // be set after the run is over, and nothing here asks it for the kernel handle disposal would free.
    private readonly ManualResetEventSlim _changed = new(initialState: false);

    private readonly int _helpersAllowed;
    private readonly Func<Action> _newHelper;
    private readonly Func<bool> _hasWork;
    private readonly Action<Action> _run;
    private readonly ThreadAllowance? _shared;
    private readonly Action _help;

    private int _helpers;
    private ExceptionDispatchInfo? _failure;

    /// <summary>
    /// Told, as each crew in the flow it is set in is made, whether the thread making it is the
    /// pool's: the seam a test pins a pass off the pool through. The thread that makes a crew is the
    /// one that drives it, and the flow carries the observer to it however the pass was started.
    /// </summary>
    internal static readonly AsyncLocal<Action<bool>?> Made = new();

    /// <param name="threads">How many threads may work at once, the calling thread included.</param>
    /// <param name="newHelper">
    /// What one helper does each time it looks for work: take work until none is left. Asked once
    /// per helper, so whatever a helper reuses lasts for every turn it takes.
    /// </param>
    /// <param name="hasWork">Whether work is waiting that a helper could take.</param>
    /// <param name="shared">
    /// Where each helper also takes a place for as long as it works, where this crew's threads count
    /// toward a bound it shares with other crews. Null where the crew's own bound is the only one. The
    /// calling thread takes no place here: it is the caller's to count, because only the caller knows
    /// when that thread starts on the work the allowance bounds.
    /// </param>
    public WorkerCrew(int threads, Func<Action> newHelper, Func<bool> hasWork, ThreadAllowance? shared = null)
        : this(threads, newHelper, hasWork, WorkerThreads.Shared.Run, shared)
    {
    }

    /// <param name="run">Runs a helper on a thread of its own: the seam a test fails a start through.</param>
    public WorkerCrew(int threads, Func<Action> newHelper, Func<bool> hasWork, Action<Action> run, ThreadAllowance? shared = null)
    {
        _helpersAllowed = threads - 1;
        _newHelper = newHelper;
        _hasWork = hasWork;
        _run = run;
        _shared = shared;
        _help = Help;

        Made.Value?.Invoke(Thread.CurrentThread.IsThreadPoolThread);
    }

    /// <summary>Whether a worker has failed, after which the run is stopping.</summary>
    public bool Failed => Volatile.Read(ref _failure) is not null;

    /// <summary>Wake the calling thread to look again.</summary>
    public void Signal() => _changed.Set();

    /// <summary>Wake the calling thread when <paramref name="ct"/> is cancelled. Dispose the result when the run ends.</summary>
    public CancellationTokenRegistration WakeOn(CancellationToken ct) =>
        ct.Register(static changed => ((ManualResetEventSlim)changed!).Set(), _changed);

    /// <summary>
    /// Wait for a <see cref="Signal"/>, unless <paramref name="ready"/> says there is already
    /// something to look at. The calling thread only.
    /// </summary>
    public void WaitUnless(Func<bool> ready)
    {
        // Reset before looking again, so a change made between the look and the wait sets the event
        // after this reset and ends the wait at once.
        _changed.Reset();
        if (!ready())
        {
            _changed.Wait();
        }
    }

    /// <summary>
    /// Start a helper, if work is waiting and the bound allows another. Never throws: a helper that
    /// cannot be started fails the run, which <see cref="Finish"/> throws, because a caller may be
    /// starting it from a read's completion, where nothing would see an exception.
    /// </summary>
    public bool TryStartHelper()
    {
        if (!TryClaimHelper())
        {
            return false;
        }

        try
        {
            _run(_help);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
        {
            // What starting a thread throws when the system will not create one, or when the thread
            // fails before it can run any code. Counted out, or Finish would wait for a helper that
            // never ran.
            _shared?.Return();
            Interlocked.Decrement(ref _helpers);
            Fail(ex);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Keep the first exception a worker raised, to be thrown on the calling thread once every helper
    /// has ended. A helper's thread catches nothing, so an exception there would end the process.
    /// </summary>
    public void Fail(Exception ex)
    {
        Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(ex), null);
        _changed.Set();
    }

    /// <summary>
    /// Wait until every helper has ended, then throw the first exception kept, unchanged. As
    /// <c>Parallel.ForEach</c> does, nothing is thrown and nothing returned while a helper is still
    /// at work, because it may still be inside a caller's callback.
    /// </summary>
    public void Finish()
    {
        while (Volatile.Read(ref _helpers) > 0)
        {
            _changed.Reset();
            if (Volatile.Read(ref _helpers) > 0)
            {
                _changed.Wait();
            }
        }

        _failure?.Throw();
    }

    private void Help()
    {
        Action? work = null;

        do
        {
            try
            {
                work ??= _newHelper();
                work();
            }
            catch (Exception ex)
            {
                // Held for the calling thread, which throws it once every helper has ended.
                Fail(ex);
            }

            _shared?.Return();
            Interlocked.Decrement(ref _helpers);
            _changed.Set();

            // Work queued after the last look and before the decrement found this helper still
            // counted, and started no other. Taking the place back here is what keeps that work from
            // waiting for the calling thread alone.
        }
        while (TryClaimHelper());
    }

    private bool TryClaimHelper()
    {
        while (true)
        {
            var running = Volatile.Read(ref _helpers);
            if (running >= _helpersAllowed || !_hasWork())
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _helpers, running + 1, running) != running)
            {
                continue;
            }

            if (_shared is null || _shared.TryTake())
            {
                return true;
            }

            // Every shared place is taken by another crew. Given up rather than waited for, because the
            // thread asking may be a helper about to end or a read's completion, and the work stays
            // queued for this crew's other threads. Signalled, because Finish may have seen the count
            // this claim raised and be waiting for it to fall.
            Interlocked.Decrement(ref _helpers);
            _changed.Set();
            return false;
        }
    }
}
