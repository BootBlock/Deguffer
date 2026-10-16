using System.Collections.Concurrent;

namespace Deguffer.Core.Scanning;

/// <summary>
/// How many directory listings are in flight on each volume, across every walk in the process, and
/// the wait that keeps a walk inside the number it was tuned for.
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
/// <see cref="WorkerThreads"/> never caps its threads.</para>
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

    private Volume VolumeOf(string directory)
    {
        // Read without allocating, because a walk lists hundreds of thousands of directories and the
        // volumes it meets are a handful.
        var root = Path.GetPathRoot(directory.AsSpan());

        return _byRoot.TryGetValue(root, out var volume)
            ? volume
            : _volumes.GetOrAdd(root.ToString(), static _ => new Volume());
    }

    /// <summary>One volume's listings in flight, and the walks waiting for one to end.</summary>
    private sealed class Volume
    {
        // A monitor rather than a semaphore, because each walk waits by its own limit and a semaphore
        // has one count for every waiter. Monitor.Wait needs an object, which Lock is not.
        private readonly object _gate = new();

        private int _inFlight;
        private int _waiting;

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

        public void Enter(int limit)
        {
            lock (_gate)
            {
                while (_inFlight >= limit)
                {
                    _waiting++;
                    Monitor.Wait(_gate);
                    _waiting--;
                }

                _inFlight++;
            }
        }

        public void Leave()
        {
            lock (_gate)
            {
                _inFlight--;

                // Every waiter, not one: a waiter woken by a single pulse may be one whose lower limit
                // still holds it back, while a waiter a slot would let in sleeps on.
                if (_waiting > 0)
                {
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }
}
