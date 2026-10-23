using Deguffer.App.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// A fade is handed back in a batch already ended: one left open never completes, and the map waits
/// on its completion to let a tile answer the pointer and to take the drawings it covered away.
/// </summary>
public sealed class VisualFadeTests
{
    [Fact]
    public void AFadeIsPlayedInABatchAlreadyEnded()
    {
        var ended = CompositionThread.Run(() =>
            CompositionThread.Compositor.CreateSpriteVisual().FadeIn(TimeSpan.FromMilliseconds(150)).IsEnded);

        Assert.True(ended);
    }
}
