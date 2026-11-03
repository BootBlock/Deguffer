using System.Collections.Concurrent;
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
                    FileNames.All,
                    budget,
                    EmptyListing,
                    (_, _, _) =>
                    {
                        BoundedFileWalk.Visit<byte>(
                            [(@"Q:\inner", 0)],
                            tuning,
                            FileNames.All,
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

        Assert.Throws<IOException>(() => failing(@"\\?\Q:\gone", default, []));

        var end = await Task.Run(() => listing(@"\\?\Q:\next", default, []))
            .WaitAsync(Deadline);

        Assert.Equal(PathPresence.Present, end.Presence);
        Assert.Equal(0, budget.InFlight(@"\\?\Q:\next"));
    }

    /// <summary>
    /// A listing that ends wakes one waiter, the one it hands its slot to, rather than every waiter
    /// queued. With eight queued behind a budget of one, eight wake-ups let all eight list. Each
    /// listing lasts until the budget counts every waiter left as queued, so woken together on each
    /// listing's end they would wake up to thirty-six times between them.
    /// </summary>
    [Fact]
    public async Task AListingThatEndsWakesOnlyTheWaiterItHandsItsSlotTo()
    {
        var budget = new ListingBudget();
        var holder = new HeldListing();
        var holding = Run(budget, holder, limit: 1);
        holder.WaitUntilStarted();

        var waiters = new Task[8];
        var listed = 0;
        ListDirectory untilTheRestAreQueued = (_, _, _) =>
        {
            var left = waiters.Length - Interlocked.Increment(ref listed);
            WaitUntil(() => budget.Waiting(Listed) == left);
            return ListingEnd.Listed;
        };

        for (var i = 0; i < waiters.Length; i++)
        {
            waiters[i] = Run(budget, untilTheRestAreQueued, limit: 1);
            var queued = i + 1;
            WaitUntil(() => budget.Waiting(Listed) == queued);
        }

        holder.Release();
        await Task.WhenAll([holding, .. waiters]).WaitAsync(Deadline);

        Assert.Equal(waiters.Length, budget.Wakes(Listed));
        Assert.Equal(0, budget.Waiting(Listed));
        Assert.Equal(0, budget.InFlight(Listed));
    }

    /// <summary>Waiters held back by one budget list in the order they came to wait.</summary>
    [Fact]
    public async Task WaitersListInTheOrderTheyCame()
    {
        var budget = new ListingBudget();
        var holder = new HeldListing();
        var holding = Run(budget, holder, limit: 1);
        holder.WaitUntilStarted();

        var order = new ConcurrentQueue<string>();
        var waiters = new Task[8];
        for (var i = 0; i < waiters.Length; i++)
        {
            waiters[i] = Run(budget, Recording(order, $"{i}"), limit: 1);
            var queued = i + 1;
            WaitUntil(() => budget.Waiting(Listed) == queued);
        }

        holder.Release();
        await Task.WhenAll([holding, .. waiters]).WaitAsync(Deadline);

        Assert.Equal(Enumerable.Range(0, waiters.Length).Select(i => $"{i}"), order);
    }

    /// <summary>
    /// A listing that comes after a slot is given back does not take it from the waiter it was given
    /// to. The thread that gave it back lists again at once, which a slot left free for the waiter to
    /// race for would let it win.
    /// </summary>
    [Fact]
    public async Task AListingThatComesLaterDoesNotOvertakeOneThatWaited()
    {
        var budget = new ListingBudget();
        var holder = new HeldListing();
        var order = new ConcurrentQueue<string>();

        var holding = Task.Factory.StartNew(
            () =>
            {
                budget.Around(holder.List, limit: 1)(Listed, default, []);
                budget.Around(Recording(order, "later"), limit: 1)(Listed, default, []);
            },
            TaskCreationOptions.LongRunning);

        holder.WaitUntilStarted();
        var waiter = Run(budget, Recording(order, "waited"), limit: 1);
        WaitUntil(() => budget.Waiting(Listed) == 1);

        holder.Release();
        await Task.WhenAll(holding, waiter).WaitAsync(Deadline);

        Assert.Equal(["waited", "later"], order);
    }

    /// <summary>
    /// A waiter held back by a lower limit is passed over for one queued after it that a freed slot
    /// admits, stays queued without waking while the count holds it back, and lists once the count
    /// falls below its limit. Two wake-ups list both: one for each waiter.
    /// </summary>
    [Fact]
    public async Task AWaiterWithALowerLimitIsPassedOverThenListsOnceTheCountFallsBelowIt()
    {
        var budget = new ListingBudget();
        var first = new HeldListing();
        var second = new HeldListing();
        var higher = new HeldListing();
        var lower = new HeldListing();

        var firstTask = Run(budget, first, limit: 2);
        var secondTask = Run(budget, second, limit: 2);
        first.WaitUntilStarted();
        second.WaitUntilStarted();

        var lowerTask = Run(budget, lower, limit: 1);
        WaitUntil(() => budget.Waiting(Listed) == 1);
        var higherTask = Run(budget, higher, limit: 2);
        WaitUntil(() => budget.Waiting(Listed) == 2);

        first.Release();
        higher.WaitUntilStarted();
        await firstTask.WaitAsync(Deadline);
        WaitUntil(() => budget.Waiting(Listed) == 1);
        Assert.False(lower.Started);

        second.Release();
        await secondTask.WaitAsync(Deadline);
        WaitUntil(() => budget.Waiting(Listed) == 1);
        Assert.False(lower.Started);

        higher.Release();
        lower.WaitUntilStarted();
        lower.Release();
        await Task.WhenAll(higherTask, lowerTask).WaitAsync(Deadline);

        Assert.Equal(2, budget.Wakes(Listed));
        Assert.Equal(0, budget.InFlight(Listed));
    }

    private static Task Walk(ListingBudget budget, WaitingTree tree, string root, int threads) =>
        Task.Factory.StartNew(
            () => BoundedFileWalk.Visit<byte>(
                [(root, 0)],
                new WalkTuning(threads, WalkTuning.MinimumListingBuffer),
                FileNames.All,
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

    private static ListingEnd EmptyListing(string directory, ListingRequest request, List<WalkEntry> into) =>
        ListingEnd.Listed;

    /// <summary>The directory every listing outside a walk lists, so all of them share one volume.</summary>
    private const string Listed = @"\\?\Q:\listed";

    /// <summary>One listing through <paramref name="budget"/> on a thread of its own, as a walk's helper lists.</summary>
    private static Task Run(ListingBudget budget, ListDirectory list, int limit) =>
        Task.Factory.StartNew(
            () => budget.Around(list, limit)(Listed, default, []),
            TaskCreationOptions.LongRunning);

    private static Task Run(ListingBudget budget, HeldListing listing, int limit) => Run(budget, listing.List, limit);

    private static ListDirectory Recording(ConcurrentQueue<string> order, string name) =>
        (_, _, _) =>
        {
            order.Enqueue(name);
            return ListingEnd.Listed;
        };

    /// <summary>
    /// Waits for <paramref name="condition"/>, which another thread makes true and nothing signals: a
    /// listing reaching the budget's queue.
    /// </summary>
    private static void WaitUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(condition, Deadline))
        {
            throw new TimeoutException("The listings never reached the state the test waits for.");
        }
    }

    /// <summary>A listing that, once it has its slot, keeps it until the test releases it.</summary>
    private sealed class HeldListing
    {
        private readonly ManualResetEventSlim _started = new();
        private readonly ManualResetEventSlim _released = new();

        public bool Started => _started.IsSet;

        public ListingEnd List(string directory, ListingRequest request, List<WalkEntry> into)
        {
            _started.Set();

            if (!_released.Wait(Deadline))
            {
                throw new TimeoutException("The test never released the listing.");
            }

            return ListingEnd.Listed;
        }

        public void WaitUntilStarted()
        {
            if (!_started.Wait(Deadline))
            {
                throw new TimeoutException("The listing never got a slot.");
            }
        }

        public void Release() => _released.Set();
    }

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

        public ListingEnd List(string directory, ListingRequest request, List<WalkEntry> into)
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
                    return ListingEnd.Listed;
                }

                for (var i = 0; i < Children; i++)
                {
                    into.Add(new WalkEntry(directory, $"d{i}", FileAttributes.Directory, 0, default, default));
                }

                return ListingEnd.Listed;
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
