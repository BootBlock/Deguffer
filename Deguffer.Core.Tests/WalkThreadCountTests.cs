using System.Diagnostics;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// A walk lists as many directories at once as it was tuned for from its first moments, not once the
/// thread pool has grown to let it.
///
/// <para>Every listing below the root blocks, as a listing does while the disk answers, until the walk
/// has as many in flight as its thread count or the test gives up. Helpers that were pool work items
/// past the pool's minimum waited for the pool's starvation detection, which adds about one thread
/// every half second, so a walk of 64 threads on a machine of fewer processors took seconds to reach
/// its count and every measurement of it was taken below the count it named.</para>
/// </summary>
public sealed class WalkThreadCountTests
{
    private const string Root = @"Z:\wide";

    /// <summary>
    /// Long enough for any machine to start the threads, however busy, and far short of the seconds
    /// the pool takes to grow to them.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task AWideTreeIsListedAtTheFullThreadCountAtOnce()
    {
        var tree = new BlockingTree(WalkTuning.MaximumThreads);

        var walk = Task.Factory.StartNew(
            () => BoundedFileWalk.Visit<byte>(
                Root,
                rootState: 0,
                new WalkTuning(WalkTuning.MaximumThreads, WalkTuning.MinimumListingBuffer),
                tree.List,
                static (_, contents, descend) =>
                {
                    foreach (var entry in contents.Entries)
                    {
                        descend(entry, 0);
                    }
                },
                static () => { },
                TimeProvider.System,
                CancellationToken.None),
            TaskCreationOptions.LongRunning);

        var reached = tree.WaitForFullCount(Deadline);
        var peak = tree.Peak;
        tree.Release();
        await walk.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            reached,
            $"{peak} of {WalkTuning.MaximumThreads} directories were being listed at once {Deadline.TotalMilliseconds} ms after the root was listed.");
        Assert.Equal(tree.Children + 1, tree.Listed);
    }

    /// <summary>
    /// A root holding twice as many directories as the walk has threads, each of which blocks while it
    /// is listed until the walk has its whole thread count listing at once.
    /// </summary>
    private sealed class BlockingTree(int threads)
    {
        private readonly ManualResetEventSlim _full = new();
        private readonly ManualResetEventSlim _released = new();
        private readonly Stopwatch _sinceRoot = new();
        private int _inFlight;
        private int _peak;
        private int _listed;

        public int Children { get; } = threads * 2;

        public int Peak => Volatile.Read(ref _peak);

        public int Listed => Volatile.Read(ref _listed);

        public ListingEnd List(string directory, EnumerationOptions options, List<WalkEntry> into)
        {
            Interlocked.Increment(ref _listed);

            if (directory == LongPath.Extended(Root))
            {
                for (var i = 0; i < Children; i++)
                {
                    into.Add(new WalkEntry(directory, $"d{i}", FileAttributes.Directory, 0, default, default));
                }

                _sinceRoot.Start();
                return new ListingEnd(PathPresence.Present, Denied: false);
            }

            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _peak, now);
            if (now == threads)
            {
                _full.Set();
            }

            _released.Wait();
            Interlocked.Decrement(ref _inFlight);

            return new ListingEnd(PathPresence.Present, Denied: false);
        }

        /// <summary>Whether the full count was listing at once within <paramref name="deadline"/> of the root's listing.</summary>
        public bool WaitForFullCount(TimeSpan deadline)
        {
            SpinWait.SpinUntil(() => _sinceRoot.IsRunning, TimeSpan.FromSeconds(10));
            var left = deadline - _sinceRoot.Elapsed;
            return _full.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }

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
