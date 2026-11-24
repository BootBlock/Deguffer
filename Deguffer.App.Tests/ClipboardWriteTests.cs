using Deguffer.App.Shell;

namespace Deguffer.App.Tests;

/// <summary>
/// A copy waits for another program to let go of the clipboard rather than report it as failed, and
/// says exactly what happened when it never lets go. That the real clipboard is reached is observed
/// by driving a page's copy.
/// </summary>
public sealed class ClipboardWriteTests
{
    private int _waits;

    /// <summary>A watcher holds the clipboard for the first tries after its content changes.</summary>
    [Fact]
    public async Task AFlushThatMeetsTheClipboardHeldIsTriedAgainUntilItGetsThrough()
    {
        var flushes = 0;

        var said = await PutAsync(set: () => true, flush: () => ++flushes > 3);

        Assert.Null(said);
        Assert.Equal(4, flushes);
        Assert.Equal(3, _waits);
    }

    [Fact]
    public async Task AClipboardHeldThroughEveryTryIsReportedAsHeld()
    {
        var sets = 0;
        var flushed = false;

        var said = await PutAsync(set: () => ++sets < 0, flush: () => flushed = true);

        Assert.Equal(ClipboardWrite.Held, said);
        Assert.Equal(ClipboardWrite.Attempts, sets);
        Assert.Equal(ClipboardWrite.Attempts - 1, _waits);
        Assert.False(flushed);
    }

    /// <summary>The text is on the clipboard once it is set, so a flush that never gets through says only that it is not kept.</summary>
    [Fact]
    public async Task AFlushThatNeverGetsThroughSaysTheTextIsCopiedButNotKept()
    {
        var flushes = 0;

        var said = await PutAsync(set: () => true, flush: () => ++flushes < 0);

        Assert.Equal(ClipboardWrite.NotKept, said);
        Assert.Equal(ClipboardWrite.Attempts, flushes);
    }

    [Fact]
    public async Task ACopyStopsWaitingWhenCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipboardWrite.PutAsync(
            () => false,
            () => true,
            ct => Task.FromCanceled(ct),
            cancelled.Token));
    }

    private Task<string?> PutAsync(Func<bool> set, Func<bool> flush) =>
        ClipboardWrite.PutAsync(set, flush, _ =>
        {
            _waits++;
            return Task.CompletedTask;
        }, CancellationToken.None);
}
