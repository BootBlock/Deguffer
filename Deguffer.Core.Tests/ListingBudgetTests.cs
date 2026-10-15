using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// Walks running beside each other share their volume's number of listings in flight, rather than
/// each listing as many as it was tuned for, so a preview that plans several providers at once cannot
/// put dozens of listings on one disk.
///
/// <para>Each test counts against a budget of its own, so no other test's walks share its count.</para>
/// </summary>
public sealed class ListingBudgetTests
{
    /// <summary>Long enough for any machine to start a walk's threads, and far short of a hung test.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a listing waits for one more listing than the budget allows before it gives up and
    /// ends, so a walk held to its budget still finishes.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(150);

    private const int Threads = 4;

    /// <summary>
    /// Two walks of one volume, each tuned for four listings, never have more than four in flight
    /// between them. Every listing below a root waits until a fifth is in flight, which only walks that
    /// each count for themselves ever reach.
    /// </summary>
    [Fact]
    public async Task TwoWalksOfOneVolumeShareItsNumberOfListings()
    {
        var budget = new ListingBudget();
        var tree = new WaitingTree(waitFor: Threads + 1);

        await Task.WhenAll(
                Walk(budget, tree, @"Q:\first", Threads),
                Walk(budget, tree, @"Q:\second", Threads))
            .WaitAsync(Deadline);

        Assert.InRange(tree.Peak, 2, Threads);
    }

    /// <summary>
    /// Each volume has a number of its own: a walk of one does not wait for a walk of another. Both are
    /// tuned for one listing, and each listing waits until the other walk's is in flight too.
    /// </summary>
    [Fact]
    public async Task WalksOfDifferentVolumesDoNotWaitForEachOther()
    {
        var budget = new ListingBudget();
        var tree = new WaitingTree(waitFor: 2);

        await Task.WhenAll(
                Walk(budget, tree, @"Q:\first", threads: 1),
                Walk(budget, tree, @"R:\second", threads: 1))
            .WaitAsync(Deadline);

        Assert.Equal(2, tree.Peak);
    }

    /// <summary>
    /// A walk started from inside another walk's callback, on the same volume and with a budget of one,
    /// finishes. A slot held while the callback ran would leave the inner walk waiting for the slot its
    /// own caller holds.
    /// </summary>
    [Fact]
    public async Task AWalkStartedInsideAnotherWalksCallbackIsNotKeptWaitingByIt()
    {
        var budget = new ListingBudget();
        var tuning = new WalkTuning(1, WalkTuning.MinimumListingBuffer);
        var innerFinished = false;

        await Task.Factory.StartNew(
                () => BoundedFileWalk.Visit<byte>(
                    [(@"Q:\outer", 0)],
                    tuning,
                    budget,
                    EmptyListing,
                    (_, _, _) =>
                    {
                        BoundedFileWalk.Visit<byte>(
                            [(@"Q:\inner", 0)],
                            tuning,
                            budget,
                            EmptyListing,
                            static (_, _, _) => { },
                            static _ => { },
                            TimeProvider.System,
                            CancellationToken.None);

                        innerFinished = true;
                    },
                    static _ => { },
                    TimeProvider.System,
                    CancellationToken.None),
                TaskCreationOptions.LongRunning)
            .WaitAsync(Deadline);

        Assert.True(innerFinished);
    }

    /// <summary>A listing that throws gives its slot back, or every later listing of the volume would wait for ever.</summary>
    [Fact]
    public async Task AListingThatThrowsGivesItsSlotBack()
    {
        var budget = new ListingBudget();
        var failing = budget.Around((_, _, _) => throw new IOException("The device is not ready."), limit: 1);
        var listing = budget.Around(EmptyListing, limit: 1);

        Assert.Throws<IOException>(() => failing(@"\\?\Q:\gone", DirectoryListing.EveryEntryWithBuffer(WalkTuning.MinimumListingBuffer), []));

        var end = await Task.Run(() => listing(@"\\?\Q:\next", DirectoryListing.EveryEntryWithBuffer(WalkTuning.MinimumListingBuffer), []))
            .WaitAsync(Deadline);

        Assert.Equal(PathPresence.Present, end.Presence);
        Assert.Equal(0, budget.InFlight(@"\\?\Q:\next"));
    }

    private static Task Walk(ListingBudget budget, WaitingTree tree, string root, int threads) =>
        Task.Factory.StartNew(
            () => BoundedFileWalk.Visit<byte>(
                [(root, 0)],
                new WalkTuning(threads, WalkTuning.MinimumListingBuffer),
                budget,
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

    private static ListingEnd EmptyListing(string directory, EnumerationOptions options, List<WalkEntry> into) =>
        new(PathPresence.Present, Denied: false);

    /// <summary>
    /// Roots holding a row of folders each, every one of which waits while it is listed until
    /// <c>waitFor</c> listings are in flight across every walk, or <see cref="Patience"/> runs out.
    /// </summary>
    private sealed class WaitingTree(int waitFor)
    {
        private const int Children = Threads * 2;

        private readonly ManualResetEventSlim _reached = new();
        private int _inFlight;
        private int _peak;

        public int Peak => Volatile.Read(ref _peak);

        public ListingEnd List(string directory, EnumerationOptions options, List<WalkEntry> into)
        {
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _peak, now);

            if (now >= waitFor)
            {
                _reached.Set();
            }

            try
            {
                if (Path.GetFileName(directory).StartsWith('d'))
                {
                    _reached.Wait(Patience);
                    return new ListingEnd(PathPresence.Present, Denied: false);
                }

                for (var i = 0; i < Children; i++)
                {
                    into.Add(new WalkEntry(directory, $"d{i}", FileAttributes.Directory, 0, default, default));
                }

                return new ListingEnd(PathPresence.Present, Denied: false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

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
