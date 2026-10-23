using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>How a drawing of the map comes in over what is under it.</summary>
internal static class VisualFade
{
    /// <summary>
    /// Fade <paramref name="visual"/> in from nothing, evenly, over <paramref name="duration"/>.
    /// Played in a batch of its own, returned already ended, whose <c>Completed</c> says the fade is over.
    /// </summary>
    public static CompositionScopedBatch FadeIn(this Visual visual, TimeSpan duration)
    {
        var compositor = visual.Compositor;
        var rise = compositor.CreateScalarKeyFrameAnimation();
        rise.InsertKeyFrame(0, 0);
        rise.InsertKeyFrame(1, 1);
        rise.Duration = duration;

        var fading = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation(nameof(Visual.Opacity), rise);
        fading.End();

        return fading;
    }
}
