using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// A walk lists as many directories at once as it was tuned for from its first moments, on threads of
/// its own, not once the thread pool has grown to let it.
///
/// <para>Helpers that were pool work items past the pool's minimum waited for the pool's starvation
/// detection, which adds about one thread every half second, so a walk of 64 threads on a machine of
/// fewer processors took seconds to reach its count and every measurement of it was taken below the
/// count it named.</para>
///
/// <para>No assertion depends on how soon the count is reached. A deadline fails on a busy machine, whose threads can be runnable
/// and still not scheduled within it, and passes against a pool that other tests have already grown.
/// The count is proven by holding every listing until the walk has its whole thread count in flight,
/// and the threads by asking each listing where it runs. The only timeout guards against a hang.</para>
/// </summary>
public sealed class WalkThreadCountTests
{
    private const string Root = @"Z:\wide";

    /// <summary>
    /// How long a walk that never reaches its count is waited for before the test fails rather than
    /// hangs. Not a measure of the walk: a walk that reaches its count does so whenever its threads are
    /// scheduled, which on a loaded machine is later than on an idle one.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AWideTreeIsListedAtTheFullThreadCountAtOnce()
    {
        var tree = await WalkAsync();

        Assert.True(
            tree.ReachedFullCount,
            $"{tree.Peak} of {WalkTuning.MaximumThreads} directories were being listed at once, and the walk never reached its thread count.");
        Assert.Equal(tree.Children + 1, tree.Listed);
    }

    [Fact]
    public async Task EveryHelperListsOnAThreadOfItsOwnRatherThanThePools()
    {
        var tree = await WalkAsync();

        Assert.True(tree.ReachedFullCount);
        Assert.Equal(0, tree.ListedByPoolHelpers);
    }

    /// <summary>
    /// Walk a <see cref="BlockingTree"/> at the largest thread count, under a budget of its own so that
    /// no other test's walk of the same volume takes a share of its listings.
    /// </summary>
    private static async Task<BlockingTree> WalkAsync()
    {
        var tree = new BlockingTree(WalkTuning.MaximumThreads);

        var walk = Task.Factory.StartNew(
            () => BoundedFileWalk.Visit<byte>(
                [(Root, 0)],
                new WalkTuning(WalkTuning.MaximumThreads, WalkTuning.MinimumListingBuffer),
                FileNames.All,
                new ListingBudget(),
                tree.List,
                static (_, contents, descend) =>
                {
                    foreach (var entry in contents.Entries)
                    {
                        descend(entry, 0);
                    }
                },
                static _ => { },
                TimeProvider.System,
                CancellationToken.None),
            TaskCreationOptions.LongRunning);

        tree.WaitForFullCount(HangGuard);
        tree.Release();
        await walk.WaitAsync(HangGuard);

        return tree;
    }

    /// <summary>
    /// A root holding twice as many directories as the walk has threads, each of which blocks while it
    /// is listed until the walk has its whole thread count listing at once, or the test lets them go.
    /// </summary>
    private sealed class BlockingTree(int threads)
    {
        private readonly ManualResetEventSlim _full = new();
        private readonly ManualResetEventSlim _released = new();
        private int _caller;
        private int _inFlight;
        private int _peak;
        private int _listed;
        private int _listedByPoolHelpers;

        public int Children { get; } = threads * 2;

        public int Peak => Volatile.Read(ref _peak);

        public int Listed => Volatile.Read(ref _listed);

        /// <summary>
        /// Listings made by a helper running on a thread-pool thread. The calling thread is the test's
        /// and lists too, so it is not counted whichever kind of thread it is.
        /// </summary>
        public int ListedByPoolHelpers => Volatile.Read(ref _listedByPoolHelpers);

        /// <summary>Whether the full count was listing at once before any listing was released.</summary>
        public bool ReachedFullCount { get; private set; }

        public ListingEnd List(string directory, ListingRequest request, List<WalkEntry> into)
        {
            Interlocked.Increment(ref _listed);

            if (directory == LongPath.Extended(Root))
            {
                // The root is the first directory taken, and only the calling thread takes it.
                Volatile.Write(ref _caller, Environment.CurrentManagedThreadId);

                for (var i = 0; i < Children; i++)
                {
                    into.Add(new WalkEntry(directory, $"d{i}", FileAttributes.Directory, 0, default, default));
                }

                return ListingEnd.Listed;
            }

            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _caller) && Thread.CurrentThread.IsThreadPoolThread)
            {
                Interlocked.Increment(ref _listedByPoolHelpers);
            }

            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _peak, now);
            if (now == threads)
            {
                _full.Set();
            }

            _released.Wait();
            Interlocked.Decrement(ref _inFlight);

            return ListingEnd.Listed;
        }

        public void WaitForFullCount(TimeSpan hangGuard) => ReachedFullCount = _full.Wait(hangGuard);

        public void Release() => _released.Set();

        private static void InterlockedMax(ref int location, int value)
        {
            var seen = Volatile.Read(ref location);
            while (value > seen)
            {
                var was = Interlocked.CompareExchange(ref location, value, seen);
                if (was == seen)
                {
                    return;
                }

                seen = was;
            }
        }
    }
}
