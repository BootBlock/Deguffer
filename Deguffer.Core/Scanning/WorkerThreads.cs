namespace Deguffer.Core.Scanning;

/// <summary>
/// The threads a <see cref="WorkerCrew"/>'s helpers run on: the process's own, each started when a
/// helper finds none free, parked between runs, and ended once it has been parked for
/// <see cref="IdleLifetime"/>.
///
/// <para><b>Not the thread pool.</b> A walk's helper spends most of its life blocked in a directory
/// listing. The pool starts threads past its minimum, one per processor, only once it finds itself
/// starved, about one every half second, so a walk of 64 threads on a machine of 32 processors, in a
/// test whose listings blocked, took 28 seconds to have 64 directories listing at once. Every measurement of the walk was taken below
/// the count it named, and the rest of the process's pool work waited behind it meanwhile. Raising
/// the pool's minimum would fix the walk by changing every other user of the pool, which is not the
/// walk's to change.</para>
///
/// <para><b>A helper never waits for a thread.</b> A parked thread takes it where there is one, and
/// a new thread starts where there is not. A set capped below what the crews ask for would leave a
/// helper a crew has counted waiting for a thread another run holds, and a crew returns only once
/// every helper it counted has ended, so a run inside another run's callback could wait for itself.
/// The bound is the crews' own: there are never more threads than helpers have run at once, and the
/// ones a while's work no longer needs end.</para>
/// </summary>
internal sealed class WorkerThreads
{
    /// <summary>
    /// How long a parked thread waits for work before it ends: the time the thread pool keeps an idle
    /// thread for. It spans the gap between one walk of a scan and the next, so a scan starts its
    /// threads once, and an application left idle holds none.
    /// </summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromSeconds(20);

    /// <summary>The set every crew runs its helpers on.</summary>
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

        Interlocked.Increment(ref _alive);

        try
        {
            new Parked(this, work).Start();
        }
        catch
        {
            // A thread the system would not start. Counted out again, so Count stays the threads that
            // exist, and raised to the crew, which counts its helper out too.
            Interlocked.Decrement(ref _alive);
            throw;
        }
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
