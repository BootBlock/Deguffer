using Deguffer.Core.Memory;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The feed keeps a memory picture current without asking the machine more than it must: one read at
/// a time, and a whole cadence after each read finishes before the next begins (§7.2). The clock is a
/// fake, so these wait on nothing but the thread pool.
/// </summary>
public sealed class MemoryFeedTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Long enough for a read the feed should not have started to have started and finished, so a test
    /// that asserts nothing happened is not merely asserting it had not happened yet.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    [Fact]
    public Task TheFirstSnapshotIsReadAtOnce() =>
        WithFeed(new FakeMemorySource(), new ManualTimeProvider(), async (feed, source, _, _) =>
        {
            Assert.True(await feed.MoveNextAsync().AsTask().WaitAsync(Patience));
            Assert.Same(FakeMemorySource.Snapshot, feed.Current);
            Assert.Equal(1, source.Reads);
        });

    [Fact]
    public Task TheNextReadWaitsForTheWholeCadence() =>
        WithFeed(new FakeMemorySource(), new ManualTimeProvider(), async (feed, source, clock, _) =>
        {
            await feed.MoveNextAsync().AsTask().WaitAsync(Patience);

            var next = feed.MoveNextAsync().AsTask();
            await clock.WhenWaitingAsync(Patience);

            clock.Advance(MemoryFeed.Cadence - TimeSpan.FromTicks(1));
            await Task.Delay(Settle);

            Assert.False(next.IsCompleted);
            Assert.Equal(1, source.Reads);

            clock.Advance(TimeSpan.FromTicks(1));

            Assert.True(await next.WaitAsync(Patience));
            Assert.Equal(2, source.Reads);
        });

    /// <summary>
    /// A read that outlasts several cadences: nothing else starts while it runs, and the wait after it
    /// is a whole cadence measured from when it finished, not from when it began.
    /// </summary>
    [Fact]
    public Task ASlowReadIsNeverOverlappedAndTheCadenceStartsWhenItEnds() =>
        WithFeed(new FakeMemorySource(), new ManualTimeProvider(), async (feed, source, clock, _) =>
        {
            await feed.MoveNextAsync().AsTask().WaitAsync(Patience);
            source.HoldFrom(read: 2);

            var second = feed.MoveNextAsync().AsTask();
            await clock.WhenWaitingAsync(Patience);
            clock.Advance(MemoryFeed.Cadence);
            await source.WhenReadStartsAsync(2, Patience);

            clock.Advance(MemoryFeed.Cadence * 5);
            await Task.Delay(Settle);

            Assert.Equal(0, clock.Waiting);
            Assert.Equal(2, source.Reads);

            source.Release();
            Assert.True(await second.WaitAsync(Patience));

            var third = feed.MoveNextAsync().AsTask();
            await clock.WhenWaitingAsync(Patience);

            clock.Advance(MemoryFeed.Cadence - TimeSpan.FromTicks(1));
            await Task.Delay(Settle);

            Assert.False(third.IsCompleted);

            clock.Advance(TimeSpan.FromTicks(1));

            Assert.True(await third.WaitAsync(Patience));
            Assert.Equal(3, source.Reads);
            Assert.Equal(1, source.MostAtOnce);
        });

    [Fact]
    public Task CancellingEndsTheFeedWithoutAnotherRead() =>
        WithFeed(new FakeMemorySource(), new ManualTimeProvider(), async (feed, source, clock, cancel) =>
        {
            await feed.MoveNextAsync().AsTask().WaitAsync(Patience);

            var next = feed.MoveNextAsync().AsTask();
            await clock.WhenWaitingAsync(Patience);

            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(Patience));

            clock.Advance(MemoryFeed.Cadence * 3);
            await Task.Delay(Settle);

            Assert.Equal(1, source.Reads);
        });

    /// <summary>
    /// Run <paramref name="test"/> against a feed over <paramref name="source"/> and
    /// <paramref name="clock"/>, and end whatever it left pending, however it ended.
    ///
    /// <para>The feed is cancelled rather than disposed. Disposing an async iterator while a move is
    /// still pending throws, and in a test that has already failed, that exception would take the place
    /// of the assertion that failed. A read held by the source is released for the same reason, so a
    /// failed test does not leave a thread waiting for good.</para>
    /// </summary>
    private static async Task WithFeed(
        FakeMemorySource source,
        ManualTimeProvider clock,
        Func<IAsyncEnumerator<MemorySnapshot>, FakeMemorySource, ManualTimeProvider, CancellationTokenSource, Task> test)
    {
        using var cancel = new CancellationTokenSource();
        var feed = new MemoryFeed(source, clock).ReadAsync(cancel.Token).GetAsyncEnumerator();

        try
        {
            await test(feed, source, clock, cancel);
        }
        finally
        {
            await cancel.CancelAsync();
            source.Release();
        }
    }
}
