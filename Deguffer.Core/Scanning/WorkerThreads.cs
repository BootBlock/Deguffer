namespace Deguffer.Core.Scanning;

/// <summary>
/// The threads a <see cref="WorkerCrew"/> runs on, its helpers and the thread that drives it: the
/// process's own, each started when work finds none free, parked between runs, and ended once it has
/// been parked for <see cref="IdleLifetime"/>.
///
/// <para><b>The driving thread too</b> (<see cref="RunAsync{T}(Func{T}, CancellationToken)"/>). A crew's calling thread works and
/// waits for the whole of its run, a walk or a pass over a file table, which is seconds to minutes.
/// On the pool that is a thread held for all of it. A preview measures for up to eight providers at
/// once and builds the index of every volume it asks about, so on a machine with fewer processors
/// than that it holds more than the pool's minimum. Every run past it waits for the pool to grow
/// before it starts, and so does every other piece of the process's pool work, the continuations of
/// the app's async code included.</para>
///
/// <para><b>Not the thread pool.</b> A walk's helper spends most of its life blocked in a directory
/// listing. The pool starts threads past its minimum, one per processor, only once it finds itself
/// starved, about one every half second. A walk of 64 threads on a machine of 32 processors, in a
/// test whose listings blocked, took 29 seconds to have 64 directories listing at once, and the
/// default of up to 16 threads is past the minimum on any machine of fewer than 16 processors. A
/// walk tuned past it ran below its count, and the rest of the process's pool work waited behind it
/// meanwhile. Raising the pool's minimum would fix the walk by changing every other user of the
/// pool, which is not the walk's to change.</para>
///
/// <para><b>A helper never waits for a thread.</b> A parked thread takes it where there is one, and
/// a new thread starts where there is not. A set capped below what the crews ask for would leave a
/// helper a crew has counted waiting for a thread another run holds, and a crew returns only once
/// every helper it counted has ended, so a run inside another run's callback could wait for itself.
/// The bound is the crews' own: a thread starts only when none is parked, so the set holds about as
/// many threads as helpers and driving threads have run at once, and the ones a while's work no
/// longer needs end.</para>
/// </summary>
internal sealed class WorkerThreads
{
    /// <summary>
    /// How long a parked thread waits for work before it ends: the time the thread pool keeps an idle
    /// thread for. It spans the gap between one walk of a scan and the next, so a scan starts its
    /// threads once, and an application left idle holds none.
    /// </summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromSeconds(20);

    /// <summary>The set every crew runs on, its helpers and its driving thread.</summary>
    public static readonly WorkerThreads Shared = new(IdleLifetime);

    private readonly TimeSpan _idleLifetime;
    private readonly Lock _gate = new();

    // Guarded by _gate. The most recently parked last, so work goes to the thread parked longest
    // ago only when every other is busy, and that is the one left to end.
    private readonly List<Parked> _parked = [];

    private int _alive;

    /// <param name="idleLifetime">How long a parked thread waits for work before it ends.</param>
    public WorkerThreads(TimeSpan idleLifetime) => _idleLifetime = idleLifetime;

    /// <summary>How many threads the set holds, parked or at work.</summary>
    public int Count => Volatile.Read(ref _alive);

    /// <summary>How many of them are parked, waiting for work.</summary>
    public int Idle
    {
        get
        {
            lock (_gate)
            {
                return _parked.Count;
            }
        }
    }

    /// <summary>
    /// Run <paramref name="work"/> on a thread of its own now: a parked one, or a new one. Nothing
    /// <paramref name="work"/> throws is caught, so it must catch what it can raise, as work queued to
    /// the pool must.
    /// </summary>
    public void Run(Action work)
    {
        Parked? thread = null;

        lock (_gate)
        {
            if (_parked.Count > 0)
            {
                thread = _parked[^1];
                _parked.RemoveAt(_parked.Count - 1);
            }
        }

        if (thread is not null)
        {
            thread.Give(work);
            return;
        }

        // Counted once started, so a thread the system would not start is never counted. With a short
        // idle lifetime the thread may end before this, and the count is one low only until it runs.
        new Parked(this, work).Start();
        Interlocked.Increment(ref _alive);
    }

    /// <summary>
    /// Run <paramref name="work"/> on a thread of its own now, as <see cref="Run"/> does, and hand back
    /// what it returns or throws as a task, as <see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>
    /// would: for work that holds its thread for a whole walk or pass.
    ///
    /// <para>Already cancelled where <paramref name="ct"/> is cancelled before the work starts, and
    /// cancelled rather than faulted where the work stops for <paramref name="ct"/>. It runs under the
    /// caller's context, as a task started by the caller does: what a provider gathers in an
    /// <see cref="AsyncLocal{T}"/> while it measures, and the culture it formats with, go with it. The
    /// thread itself keeps none of it once the work ends.</para>
    ///
    /// <para>The task's continuations run on the pool, never on this thread, which goes back to the set
    /// at once rather than running whatever the caller does next.</para>
    ///
    /// <para>A thread the system will not start faults the task rather than throwing here, as
    /// <see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/> never throws for want of a
    /// thread. A caller that opened something for the work to close, or started a rival to cancel,
    /// learns of the failure where it awaits, as it would of any other.</para>
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct) => RunAsync(Run, work, ct);

    /// <summary><see cref="RunAsync{T}(Func{T}, CancellationToken)"/> on <paramref name="run"/>, the seam a test fails a thread start through.</summary>
    internal static Task<T> RunAsync<T>(Action<Action> run, Func<T> work, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = ExecutionContext.Capture();

        void Finish()
        {
            try
            {
                done.SetResult(work());
            }
            catch (OperationCanceledException stopped) when (stopped.CancellationToken == ct && ct.IsCancellationRequested)
            {
                done.SetCanceled(ct);
            }
            catch (Exception failure)
            {
                // Not swallowed: handed to whoever awaits the task, as Task.Run hands it. Nothing may
                // escape to the thread, where it would end the process.
                done.SetException(failure);
            }
        }

        try
        {
            run(context is null ? Finish : () => ExecutionContext.Run(context, static finish => ((Action)finish!)(), (Action)Finish));
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
        {
            // What starting a thread throws when the system will not create one, or when the thread
            // fails before it can run any code, as WorkerCrew meets it for a helper.
            done.SetException(ex);
        }

        return done.Task;
    }

    /// <summary>One thread of the set, and the work it is handed while parked.</summary>
    private sealed class Parked(WorkerThreads set, Action first)
    {
        private readonly SemaphoreSlim _given = new(initialCount: 0);

        // Written before the release that wakes the thread, and read after the wait that release
        // ends, so the wait's barrier makes it visible.
        private Action? _work = first;

        public void Start()
        {
            // Unsafe so the thread does not run every later piece of work under the context of the
            // caller that happened to start it. Work handed over carries no caller's context.
            new Thread(Work) { IsBackground = true, Name = "Deguffer worker" }.UnsafeStart();
        }

        public void Give(Action work)
        {
            _work = work;
            _given.Release();
        }

        private void Work()
        {
            while (true)
            {
                var work = _work!;
                _work = null;
                work();

                lock (set._gate)
                {
                    set._parked.Add(this);
                }

                if (!_given.Wait(set._idleLifetime) && TryEnd())
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Leave the set, unless a caller took this thread from it after the wait gave up and before
        /// this looked, in which case its work is on the way and is waited for.
        /// </summary>
        private bool TryEnd()
        {
            bool taken;
            lock (set._gate)
            {
                taken = !set._parked.Remove(this);
            }

            if (taken)
            {
                _given.Wait();
                return false;
            }

            Interlocked.Decrement(ref set._alive);
            _given.Dispose();
            return true;
        }
    }
}
