using System.Collections.Concurrent;

namespace Deguffer.Core.Scanning;

/// <summary>
/// How many directory listings are in flight on each volume, across every walk in the process, and
/// the queue that keeps a walk inside the number it was tuned for.
///
/// <para><b>Why one for the process.</b> A walk's <see cref="WalkTuning.Threads"/> bounds its own
/// listings, but a preview plans several providers at once, and each measures its tree with a walk of
/// its own. Bounded only per walk, five walks of sixteen threads would list eighty directories of one
/// disk at once, where sixteen is what the disk was measured to want. Here a walk lists only while
/// fewer than its own thread count are in flight on the volume, whoever's they are, so walks running
/// together share the disk's number rather than multiplying it.</para>
///
/// <para><b>Held only around the listing.</b> A slot is taken as the listing call starts and given
/// back as it returns, before the walk hands the contents to its caller. A caller's callback may
/// start a walk of its own, and a slot held through that callback would leave the inner walk waiting
/// for a slot its own caller holds. Held for no longer than a listing, every wait ends when the
/// listings in flight end, so none can wait for itself. That is the same reason
/// <see cref="WorkerThreads"/> never caps its threads, and <see cref="Volume"/> says why handing a
/// slot to one waiter keeps it true.</para>
///
/// <para><b>Keyed by the root of the path</b>, as <see cref="Path.GetPathRoot(ReadOnlySpan{char})"/>
/// reads it: a drive, a share, or a volume named by its GUID. A volume mounted in a folder of another
/// is counted with the volume that holds the folder. A walk never descends through a reparse point,
/// so it reaches such a volume only when one of its roots is inside it, which no provider's root
/// is.</para>
/// </summary>
internal sealed class ListingBudget
{
    /// <summary>The budget every walk lists under.</summary>
    public static ListingBudget Shared { get; } = new();

    private readonly ConcurrentDictionary<string, Volume> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Volume>.AlternateLookup<ReadOnlySpan<char>> _byRoot;

    public ListingBudget() => _byRoot = _volumes.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// <paramref name="list"/>, waiting before each listing until fewer than <paramref name="limit"/>
    /// listings are in flight on the directory's volume.
    /// </summary>
    /// <param name="limit">
    /// The walk's own thread count, the number of listings its volume was tuned for. Each walk waits by
    /// its own: two walks of one volume tuned alike share one number, and a walk tuned lower, for a disk
    /// whose head concurrent reads compete for, holds back while a walk tuned higher runs.
    /// </param>
    public ListDirectory Around(ListDirectory list, int limit)
    {
        return (directory, request, into) =>
        {
            var volume = VolumeOf(directory);
            volume.Enter(limit);

            try
            {
                return list(directory, request, into);
            }
            finally
            {
                volume.Leave();
            }
        };
    }

    /// <summary>How many listings are in flight on the volume holding <paramref name="path"/>.</summary>
    public int InFlight(string path) => VolumeOf(path).InFlight;

    /// <summary>How many listings are waiting for a slot on the volume holding <paramref name="path"/>.</summary>
    public int Waiting(string path) => VolumeOf(path).Waiting;

    /// <summary>
    /// How many times a listing that waited for a slot on the volume holding <paramref name="path"/>
    /// woke. Each wait wakes once, when its slot is handed to it, so a count above the waits is
    /// wake-ups that did no work.
    /// </summary>
    public long Wakes(string path) => VolumeOf(path).Wakes;

    private Volume VolumeOf(string directory)
    {
        // Read without allocating, because a walk lists hundreds of thousands of directories and the
        // volumes it meets are a handful.
        var root = Path.GetPathRoot(directory.AsSpan());

        return _byRoot.TryGetValue(root, out var volume)
            ? volume
            : _volumes.GetOrAdd(root.ToString(), static _ => new Volume());
    }

    /// <summary>
    /// One volume's listings in flight, and the listings waiting, in the order they came, for a slot.
    ///
    /// <para><b>A slot is handed over, not raced for.</b> A listing that ends gives its slot straight to
    /// the first waiter whose limit admits it, counts that waiter in flight, and wakes it alone. Woken
    /// together, every waiter would take the lock in turn only to find the slot gone, so one listing would
    /// cost a context switch for each thread held back on the volume. A waiter whose lower limit still
    /// holds it back stays queued, and the first slot that brings the count below its limit is its.</para>
    ///
    /// <para><b>Why no wait can last for ever.</b> No queued waiter's limit is ever above the count in
    /// flight: a listing queues only when its limit holds it back, and each slot given back goes to a
    /// waiter it admits before anything else can take it. So a slot never stands free while a waiter it
    /// would admit sleeps, and a listing that comes later never takes a slot a waiter could have had. A
    /// waiter woken already holds its slot and runs a listing, which waits for nothing, then gives the
    /// slot back in its turn. Every wait still ends when the listings in flight end.</para>
    /// </summary>
    private sealed class Volume
    {
        private readonly Lock _gate = new();

        private Waiter? _first;
        private Waiter? _last;
        private int _inFlight;
        private int _waiting;
        private long _wakes;

        public int InFlight
        {
            get
            {
                lock (_gate)
                {
                    return _inFlight;
                }
            }
        }

        public int Waiting
        {
            get
            {
                lock (_gate)
                {
                    return _waiting;
                }
            }
        }

        public long Wakes => Interlocked.Read(ref _wakes);

        public void Enter(int limit)
        {
            Waiter waiter;

            lock (_gate)
            {
                if (_inFlight < limit)
                {
                    _inFlight++;
                    return;
                }

                waiter = Waiter.OfThisThread;
                waiter.Limit = limit;
                Append(waiter);
            }

            // Leave counted this listing in flight before it set the event, so there is nothing to
            // check on waking: the slot is already this listing's.
            waiter.Handed.Wait();
            waiter.Handed.Reset();
            Interlocked.Increment(ref _wakes);
        }

        public void Leave()
        {
            Waiter? next = null;

            lock (_gate)
            {
                _inFlight--;

                for (var waiter = _first; waiter is not null; waiter = waiter.Next)
                {
                    if (_inFlight < waiter.Limit)
                    {
                        Remove(waiter);
                        _inFlight++;
                        next = waiter;
                        break;
                    }
                }
            }

            // Set outside the lock, so the thread it wakes does not wake into a lock still held.
            next?.Handed.Set();
        }

        private void Append(Waiter waiter)
        {
            waiter.Previous = _last;
            waiter.Next = null;

            if (_last is null)
            {
                _first = waiter;
            }
            else
            {
                _last.Next = waiter;
            }

            _last = waiter;
            _waiting++;
        }

        private void Remove(Waiter waiter)
        {
            if (waiter.Previous is null)
            {
                _first = waiter.Next;
            }
            else
            {
                waiter.Previous.Next = waiter.Next;
            }

            if (waiter.Next is null)
            {
                _last = waiter.Previous;
            }
            else
            {
                waiter.Next.Previous = waiter.Previous;
            }

            waiter.Previous = null;
            waiter.Next = null;
            _waiting--;
        }
    }

    /// <summary>
    /// A thread's place in a volume's queue, and the event its slot is handed over by.
    ///
    /// <para>One per thread, made the first time the thread waits and reused for every wait after,
    /// because a walk's thread may wait before each of thousands of listings. A thread waits for one
    /// slot at a time, since it waits only inside a listing call and lists nothing while it waits, so it
    /// is in at most one queue at once.</para>
    /// </summary>
    private sealed class Waiter
    {
        [ThreadStatic]
        private static Waiter? s_ofThisThread;

        public static Waiter OfThisThread => s_ofThisThread ??= new Waiter();

        // No spin before blocking. A waiter is held back because the volume's slots are taken, and
        // waiters spinning take the cores those listings run on: eight walks of sixteen threads on one
        // volume ran slower, and on more processor time, with the default spin.
        public ManualResetEventSlim Handed { get; } = new(false, spinCount: 0);

        public int Limit { get; set; }

        public Waiter? Previous { get; set; }

        public Waiter? Next { get; set; }
    }
}
