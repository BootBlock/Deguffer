using System.Numerics;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Deguffer.App.Shell;

/// <summary>
/// One part of a screen coming in: it rises a short way into place as it fades in, or only fades
/// where its motion does not travel. A page arriving brings each of its parts in this way
/// (<see cref="PageArrival"/>), and so do the figures a finished clean leaves on the Storage page.
///
/// <para>On the compositor, through the part's own visual, so once it starts it costs the UI thread
/// nothing per frame, and it holds up no input: a visual's opacity and translation change what is
/// drawn, never where the part is laid out, so a click during the entrance lands on what it would
/// have anyway.</para>
/// </summary>
/// <param name="rise">How far below its place the part starts, in device-independent pixels.</param>
internal sealed class ElementEntrance(float rise)
{
    private CompositionEasingFunction? _ease;

    /// <summary>
    /// Put <paramref name="part"/> where its entrance starts, unseen. In place when nothing travels,
    /// because a part held for an entrance that never played can come back with motion since turned off.
    /// </summary>
    public void Hold(UIElement part, Motion played)
    {
        var visual = ElementCompositionPreview.GetElementVisual(part);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Opacity = 0;

        ElementCompositionPreview.SetIsTranslationEnabled(part, true);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", played.Travels ? new Vector3(0, rise, 0) : Vector3.Zero);
    }

    /// <summary>Bring <paramref name="part"/> in, starting <paramref name="delay"/> from now.</summary>
    public void Enter(UIElement part, Motion played, TimeSpan delay)
    {
        var visual = ElementCompositionPreview.GetElementVisual(part);
        var compositor = visual.Compositor;

        // The curve Motion eases by, so a part arrives as every other move here does.
        var ease = _ease ??= compositor.CreateCubicBezierEasingFunction(
            Motion.EaseControlPoints.First,
            Motion.EaseControlPoints.Second);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        Time(fade, played, delay);
        visual.StartAnimation(nameof(Visual.Opacity), fade);

        // Enabled whether or not this entrance travels, because a hold before it may have left a
        // translation that has to be put back.
        ElementCompositionPreview.SetIsTranslationEnabled(part, true);

        if (!played.Travels)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }

        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, new Vector3(0, rise, 0));
        move.InsertKeyFrame(1, Vector3.Zero, ease);
        Time(move, played, delay);
        visual.StartAnimation("Translation", move);
    }

    /// <summary>
    /// Played for the motion's length after <paramref name="delay"/>, held at its first frame from the
    /// start, so a part waiting its turn is not drawn in place for the length of the wait first.
    /// </summary>
    private static void Time(KeyFrameAnimation animation, Motion played, TimeSpan delay)
    {
        animation.Duration = played.Duration;
        animation.DelayTime = delay;
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
    }
}
