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
    public async Task TheFirstSnapshotIsReadAtOnce()
    {
        var source = new FakeMemorySource();
        await using var feed = new MemoryFeed(source, new ManualTimeProvider()).ReadAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await feed.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Same(FakeMemorySource.Snapshot, feed.Current);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task TheNextReadWaitsForTheWholeCadence()
    {
        var source = new FakeMemorySource();
        var clock = new ManualTimeProvider();
        await using var feed = new MemoryFeed(source, clock).ReadAsync(CancellationToken.None).GetAsyncEnumerator();

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
    }

    /// <summary>
    /// A read that outlasts several cadences: nothing else starts while it runs, and the wait after it
    /// is a whole cadence measured from when it finished, not from when it began.
    /// </summary>
    [Fact]
    public async Task ASlowReadIsNeverOverlappedAndTheCadenceStartsWhenItEnds()
    {
        var source = new FakeMemorySource();
        var clock = new ManualTimeProvider();
        await using var feed = new MemoryFeed(source, clock).ReadAsync(CancellationToken.None).GetAsyncEnumerator();

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
    }

    [Fact]
    public async Task CancellingEndsTheFeedWithoutAnotherRead()
    {
        var source = new FakeMemorySource();
        var clock = new ManualTimeProvider();
        using var cancel = new CancellationTokenSource();
        await using var feed = new MemoryFeed(source, clock).ReadAsync(cancel.Token).GetAsyncEnumerator();

        await feed.MoveNextAsync().AsTask().WaitAsync(Patience);

        var next = feed.MoveNextAsync().AsTask();
        await clock.WhenWaitingAsync(Patience);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(Patience));

        clock.Advance(MemoryFeed.Cadence * 3);
        await Task.Delay(Settle);

        Assert.Equal(1, source.Reads);
    }
}
