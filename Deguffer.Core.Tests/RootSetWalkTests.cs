using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// Several roots in one walk: every thread busy from the start, each directory listed once however
/// many roots it lies in, and a root the outer walk cannot reach still walked.
///
/// <para>The trees here are listed through the walk's listing seam, so what is listed, and how much
/// at once, is counted rather than inferred from a total.</para>
/// </summary>
public sealed class RootSetWalkTests
{
    private const int Threads = 8;

    /// <summary>
    /// Long enough for any machine to start the threads, however busy. A walk that left the roots to
    /// the calling thread never reaches the count at all.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A list of folders with no folder inside them queues nothing as it is read, so the walk has to
    /// start its helpers for the roots themselves, or every root is read on the calling thread, one
    /// after another, which is the cost one walk for every root exists to remove.
    /// </summary>
    [Fact]
    public async Task ManyFoldersHoldingNoFolderAreListedAtTheFullThreadCountAtOnce()
    {
        var tree = new BlockingLeaves(Threads);
        string[] leaves = [.. Enumerable.Range(0, Threads * 2).Select(i => $@"Z:\leaf{i}")];

        var walk = Task.Factory.StartNew(
            () => RootSetWalk.Visit<object>(
                [.. leaves.Select(leaf => new WalkRoot(LongPath.Extended(leaf), LeftOut: null))],
                new WalkTuning(Threads, WalkTuning.MinimumListingBuffer),
                tree.List,
                static (_, _, _, _) => new object(),
                static () => { },
                TimeProvider.System,
                CancellationToken.None),
            TaskCreationOptions.LongRunning);

        var reached = tree.WaitForFullCount(Deadline);
        var peak = tree.Peak;
        tree.Release();
        await walk.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(reached, $"{peak} of {Threads} folders were being listed at once.");
        Assert.Equal(leaves.Length, tree.Listed);
    }

    /// <summary>
    /// A root inside another is met by the outer walk and joins it there: each directory is listed
    /// once, and visited once for each root it lies in, the inner root's own folder as that root's top.
    /// Asked inner first, so the outcome does not depend on the order the roots were given in.
    /// </summary>
    [Fact]
    public void ARootInsideAnotherIsListedOnceAndVisitedForBoth()
    {
        var tree = new FakeTree()
            .Folder(@"Z:\a", ("b", FileAttributes.Directory), ("one.bin", FileAttributes.Normal))
            .Folder(@"Z:\a\b", ("c", FileAttributes.Directory), ("two.bin", FileAttributes.Normal))
            .Folder(@"Z:\a\b\c", ("three.bin", FileAttributes.Normal));

        var visits = Walk(tree, @"Z:\a\b", @"Z:\a");

        Assert.Equal(
            [
                (0, @"Z:\a\b", true),
                (0, @"Z:\a\b\c", false),
                (1, @"Z:\a", true),
                (1, @"Z:\a\b", false),
                (1, @"Z:\a\b\c", false),
            ],
            visits);
        Assert.All(tree.Listings, listing => Assert.Equal(1, listing.Value));
        Assert.Equal(3, tree.Listings.Count);
    }

    /// <summary>
    /// The same path asked twice is one place to walk, with both roots starting there.
    /// </summary>
    [Fact]
    public void APathAskedTwiceIsListedOnceAndVisitedForEach()
    {
        var tree = new FakeTree()
            .Folder(@"Z:\a", ("one.bin", FileAttributes.Normal));

        var visits = Walk(tree, @"Z:\a", @"z:\A\");

        Assert.Equal([(0, @"Z:\a", true), (1, @"Z:\a", true)], visits);
        Assert.Equal(1, tree.Listings[LongPath.Extended(@"Z:\a")]);
    }

    /// <summary>
    /// A root the outer walk cannot enter, because the folder holding it is a link the walk never
    /// follows, is walked on its own afterwards, exactly as it would have been asked about alone. Left
    /// waiting for an outer walk that never meets it, it would measure nothing.
    /// </summary>
    [Fact]
    public void ARootBehindALinkTheOuterWalkDoesNotFollowIsWalkedOnItsOwn()
    {
        var tree = new FakeTree()
            .Folder(@"Z:\a", ("link", FileAttributes.Directory | FileAttributes.ReparsePoint), ("one.bin", FileAttributes.Normal))
            .Folder(@"Z:\a\link", ("inner", FileAttributes.Directory))
            .Folder(@"Z:\a\link\inner", ("two.bin", FileAttributes.Normal));

        var visits = Walk(tree, @"Z:\a", @"Z:\a\link\inner");

        Assert.Equal([(0, @"Z:\a", true), (1, @"Z:\a\link\inner", true)], visits);
        Assert.False(tree.Listings.ContainsKey(LongPath.Extended(@"Z:\a\link")));
    }

    /// <summary>Each visit as the root it was for, the folder, and whether it was that root's top.</summary>
    private static List<(int Root, string Folder, bool IsTop)> Walk(FakeTree tree, params string[] roots)
    {
        var visits = new ConcurrentBag<(int, string, bool)>();

        RootSetWalk.Visit<string>(
            [.. roots.Select(root => WalkRoot.Of(root, default))],
            new WalkTuning(Threads, WalkTuning.MinimumListingBuffer),
            tree.List,
            (root, holder, contents, _) =>
            {
                var folder = LongPath.Display(contents.Entries[0].Directory);
                visits.Add((root, folder, holder is null));
                return folder;
            },
            static () => { },
            TimeProvider.System,
            CancellationToken.None);

        return [.. visits.Order()];
    }

    /// <summary>A tree no volume holds, counting how often each folder is listed.</summary>
    private sealed class FakeTree
    {
        private readonly Dictionary<string, (string Name, FileAttributes Attributes)[]> _folders = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<string, int> Listings { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FakeTree Folder(string path, params (string Name, FileAttributes Attributes)[] children)
        {
            _folders[LongPath.Extended(path)] = children;
            return this;
        }

        public ListingEnd List(string directory, EnumerationOptions options, List<WalkEntry> into)
        {
            Listings.AddOrUpdate(directory, 1, (_, count) => count + 1);

            if (!_folders.TryGetValue(directory, out var children))
            {
                return new ListingEnd(PathPresence.Absent, Denied: false);
            }

            foreach (var (name, attributes) in children)
            {
                into.Add(new WalkEntry(directory, name, attributes, attributes.HasFlag(FileAttributes.Directory) ? 0 : 10, default, default));
            }

            return new ListingEnd(PathPresence.Present, Denied: false);
        }
    }

    /// <summary>
    /// Folders holding nothing, each of which blocks while it is listed until the walk has its whole
    /// thread count listing at once.
    /// </summary>
    private sealed class BlockingLeaves(int threads)
    {
        private readonly ManualResetEventSlim _full = new();
        private readonly ManualResetEventSlim _released = new();
        private int _inFlight;
        private int _peak;
        private int _listed;

        public int Peak => Volatile.Read(ref _peak);

        public int Listed => Volatile.Read(ref _listed);

        public ListingEnd List(string directory, EnumerationOptions options, List<WalkEntry> into)
        {
            Interlocked.Increment(ref _listed);

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

        public bool WaitForFullCount(TimeSpan deadline) => _full.Wait(deadline);

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
