using Deguffer.Core.Memory;
using Deguffer.Testing;

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
            Assert.Same(FakeMemorySource.Snapshot, feed.Current.Snapshot);
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
    /// Live update off: the cadence reads nothing however long it runs, and Refresh reads once, at
    /// once, with the time it was read.
    /// </summary>
    [Fact]
    public Task WithLiveUpdateOffOnlyRefreshReads() =>
        WithFeed(new FakeMemorySource(), new ManualTimeProvider(), async (control, feed, source, clock, _) =>
        {
            control.Live = false;

            await feed.MoveNextAsync().AsTask().WaitAsync(Patience);
            Assert.Equal(clock.GetUtcNow(), feed.Current.Read);

            var next = feed.MoveNextAsync().AsTask();

            clock.Advance(MemoryFeed.Cadence * 10);
            await Task.Delay(Settle);

            Assert.False(next.IsCompleted);
            Assert.Equal(1, source.Reads);
            Assert.Equal(0, clock.Waiting);

            control.Refresh();

            Assert.True(await next.WaitAsync(Patience));
            Assert.Equal(2, source.Reads);
            Assert.Equal(clock.GetUtcNow(), feed.Current.Read);
        });

    /// <summary>Live, Refresh does not wait for the rest of the cadence, and leaves no beat behind it.</summary>
    [Fact]
    public Task RefreshReadsAtOnceWhileLive() =>
        WithFeed(new FakeMemorySource(), new ManualTimeProvider(), async (control, feed, source, clock, _) =>
        {
            await feed.MoveNextAsync().AsTask().WaitAsync(Patience);

            var next = feed.MoveNextAsync().AsTask();
            await clock.WhenWaitingAsync(Patience);

            control.Refresh();

            Assert.True(await next.WaitAsync(Patience));
            Assert.Equal(2, source.Reads);
            Assert.Equal(0, clock.Waiting);
        });

    /// <summary>
    /// Turned on, the feed reads at once, because what is on screen may be as old as the last press,
    /// and goes on reading on the cadence from there.
    /// </summary>
    [Fact]
    public Task TurningLiveUpdateOnReadsAtOnceAndResumesTheCadence() =>
        WithFeed(new FakeMemorySource(), new ManualTimeProvider(), async (control, feed, source, clock, _) =>
        {
            control.Live = false;
            await feed.MoveNextAsync().AsTask().WaitAsync(Patience);

            var next = feed.MoveNextAsync().AsTask();
            await Task.Delay(Settle);
            Assert.False(next.IsCompleted);

            control.Live = true;

            Assert.True(await next.WaitAsync(Patience));
            Assert.Equal(2, source.Reads);

            var after = feed.MoveNextAsync().AsTask();
            await clock.WhenWaitingAsync(Patience);
            clock.Advance(MemoryFeed.Cadence);

            Assert.True(await after.WaitAsync(Patience));
            Assert.Equal(3, source.Reads);
        });

    /// <summary>Turned off part way through a cadence, the read it was counting down to never happens.</summary>
    [Fact]
    public Task TurningLiveUpdateOffCallsOffTheNextBeat() =>
        WithFeed(new FakeMemorySource(), new ManualTimeProvider(), async (control, feed, source, clock, _) =>
        {
            await feed.MoveNextAsync().AsTask().WaitAsync(Patience);

            var next = feed.MoveNextAsync().AsTask();
            await clock.WhenWaitingAsync(Patience);

            control.Live = false;
            await Task.Delay(Settle);

            clock.Advance(MemoryFeed.Cadence * 10);
            await Task.Delay(Settle);

            Assert.False(next.IsCompleted);
            Assert.Equal(1, source.Reads);
            Assert.Equal(0, clock.Waiting);
        });

    /// <summary>
    /// A press during a read is not answered by that read, which began before the press: one more read
    /// follows it. Several presses during it are still one read, and never two at once.
    /// </summary>
    [Fact]
    public Task PressesDuringAReadAskForOneReadAfterIt() =>
        WithFeed(new FakeMemorySource(), new ManualTimeProvider(), async (control, feed, source, _, _) =>
        {
            control.Live = false;
            await feed.MoveNextAsync().AsTask().WaitAsync(Patience);
            source.HoldFrom(read: 2);

            var second = feed.MoveNextAsync().AsTask();
            control.Refresh();
            await source.WhenReadStartsAsync(2, Patience);

            control.Refresh();
            control.Refresh();
            source.Release();

            Assert.True(await second.WaitAsync(Patience));

            var third = feed.MoveNextAsync().AsTask();
            Assert.True(await third.WaitAsync(Patience));
            Assert.Equal(3, source.Reads);

            var fourth = feed.MoveNextAsync().AsTask();
            await Task.Delay(Settle);

            Assert.False(fourth.IsCompleted);
            Assert.Equal(3, source.Reads);
            Assert.Equal(1, source.MostAtOnce);
        });

    /// <summary>
    /// A page the reader comes back to with live update off shows what it showed, and does not read
    /// until they ask: coming back is not asking. The reading counts as shown once the page has asked
    /// for the one after it, which is what a page does when it has finished showing one.
    /// </summary>
    [Fact]
    public async Task ReadingAgainWithLiveUpdateOffWaitsForRefreshOnceAReadingWasTaken()
    {
        var source = new FakeMemorySource();
        var control = new MemoryFeed(source, new ManualTimeProvider()) { Live = false };

        using (var first = new CancellationTokenSource())
        {
            var visit = control.ReadAsync(first.Token).GetAsyncEnumerator();

            Assert.True(await visit.MoveNextAsync().AsTask().WaitAsync(Patience));

            var waiting = visit.MoveNextAsync().AsTask();
            await first.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience));
        }

        using var second = new CancellationTokenSource();
        var again = control.ReadAsync(second.Token).GetAsyncEnumerator();

        try
        {
            var next = again.MoveNextAsync().AsTask();
            await Task.Delay(Settle);

            Assert.False(next.IsCompleted);
            Assert.Equal(1, source.Reads);

            control.Refresh();

            Assert.True(await next.WaitAsync(Patience));
            Assert.Equal(2, source.Reads);
        }
        finally
        {
            await second.CancelAsync();
        }
    }

    /// <summary>
    /// A reading the page threw away because it was left while the read ran was never on screen, so a
    /// page come back to with live update off reads at once rather than standing empty until the
    /// reader thinks to press Refresh.
    /// </summary>
    [Fact]
    public async Task AReadingThrownAwayUnseenDoesNotHoldTheNextVisitBack()
    {
        var source = new FakeMemorySource();
        var control = new MemoryFeed(source, new ManualTimeProvider()) { Live = false };

        using (var first = new CancellationTokenSource())
        {
            var visit = control.ReadAsync(first.Token).GetAsyncEnumerator();

            Assert.True(await visit.MoveNextAsync().AsTask().WaitAsync(Patience));
            await first.CancelAsync();
        }

        using var second = new CancellationTokenSource();
        var again = control.ReadAsync(second.Token).GetAsyncEnumerator();

        try
        {
            Assert.True(await again.MoveNextAsync().AsTask().WaitAsync(Patience));
            Assert.Equal(2, source.Reads);
        }
        finally
        {
            await second.CancelAsync();
        }
    }

    /// <summary>
    /// Run <paramref name="test"/> against a feed over <paramref name="source"/> and
    /// <paramref name="clock"/>, and end whatever it left pending, however it ended.
    ///
    /// <para>The feed is cancelled rather than disposed. Disposing an async iterator while a move is
    /// still pending throws, and in a test that has already failed, that exception would take the place
    /// of the assertion that failed. A read held by the source is released for the same reason, so a
    /// failed test does not leave a thread waiting for good.</para>
    /// </summary>
    private static Task WithFeed(
        FakeMemorySource source,
        ManualTimeProvider clock,
        Func<IAsyncEnumerator<MemoryReading>, FakeMemorySource, ManualTimeProvider, CancellationTokenSource, Task> test) =>
        WithFeed(source, clock, (_, feed, held, time, cancel) => test(feed, held, time, cancel));

    /// <summary>
    /// As above, with the feed itself handed over as well, for a test that turns live update on and
    /// off or presses Refresh while the sequence runs.
    /// </summary>
    private static async Task WithFeed(
        FakeMemorySource source,
        ManualTimeProvider clock,
        Func<MemoryFeed, IAsyncEnumerator<MemoryReading>, FakeMemorySource, ManualTimeProvider, CancellationTokenSource, Task> test)
    {
        using var cancel = new CancellationTokenSource();
        var control = new MemoryFeed(source, clock);
        var feed = control.ReadAsync(cancel.Token).GetAsyncEnumerator();

        try
        {
            await test(control, feed, source, clock, cancel);
        }
        finally
        {
            await cancel.CancelAsync();
            source.Release();
        }
    }
}
