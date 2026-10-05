using System.Runtime.ExceptionServices;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The threads of one parallel run: the calling thread, which works and is the only one that waits,
/// and up to a bound of thread-pool helpers, each started when there is work for it and ended when
/// there is none.
///
/// <para>The walk (<see cref="WalkWorkers{TState}"/>) and the read of the file table
/// (<see cref="Mft.MftReadPass"/>) differ in what their work is and in when they are over. They agree
/// in how a helper starts, ends, fails and is waited for, which is what this holds.</para>
///
/// <para><b>A helper that finds no work ends</b> rather than blocking a pool thread. The calling
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

    private int _helpers;
    private ExceptionDispatchInfo? _failure;

    /// <param name="threads">How many threads may work at once, the calling thread included.</param>
    /// <param name="newHelper">
    /// What one helper does each time it looks for work: take work until none is left. Asked once
    /// per helper, so whatever a helper reuses lasts for every turn it takes.
    /// </param>
    /// <param name="hasWork">Whether work is waiting that a helper could take.</param>
    public WorkerCrew(int threads, Func<Action> newHelper, Func<bool> hasWork)
    {
        _helpersAllowed = threads - 1;
        _newHelper = newHelper;
        _hasWork = hasWork;
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

    /// <summary>Start a helper, if work is waiting and the bound allows another.</summary>
    public bool TryStartHelper()
    {
        if (!TryClaimHelper())
        {
            return false;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static crew => crew.Help(), this, preferLocal: false);
        return true;
    }

    /// <summary>
    /// Keep the first exception a worker raised, to be thrown on the calling thread once every helper
    /// has ended. A helper runs on a pool thread, where an exception would end the process.
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

            if (Interlocked.CompareExchange(ref _helpers, running + 1, running) == running)
            {
                return true;
            }
        }
    }
}
