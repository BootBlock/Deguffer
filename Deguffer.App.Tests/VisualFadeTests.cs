using Deguffer.App.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// A fade is handed back in its own batch, already ended, which completes when the fade is over: the
/// map waits on that completion to let a tile answer the pointer and to take away the drawings the
/// fade covered.
/// </summary>
public sealed class VisualFadeTests
{
    [Fact]
    public void AFadesBatchIsAlreadyEnded()
    {
        var ended = CompositionThread.Run(() =>
            CompositionThread.Compositor.CreateSpriteVisual().FadeIn(TimeSpan.FromMilliseconds(150)).IsEnded);

        Assert.True(ended);
    }

    /// <summary>
    /// The fade is played inside its batch: one started outside it leaves the batch empty, and an empty
    /// batch completes at once, while the drawing is still fading in.
    /// </summary>
    [Fact]
    public async Task AFadesBatchCompletesOnlyOnceTheFadeIsOver()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (visual, batch) = CompositionThread.Run(() =>
        {
            var visual = CompositionThread.Compositor.CreateSpriteVisual();
            var batch = visual.FadeIn(TimeSpan.FromSeconds(30));
            batch.Completed += (_, _) => completed.TrySetResult();

            return (visual, batch);
        });

        var first = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(1)));

        GC.KeepAlive(visual);
        GC.KeepAlive(batch);
        Assert.NotSame(completed.Task, first);
    }
}
