using System.Numerics;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Deguffer.App.Shell;

/// <summary>
/// The entrance a page plays as the navigation rail brings it in: its header rises a short way into
/// place as it fades in, and the rest of the page follows a step behind (<see cref="PageEntrance"/>).
///
/// <para>On the compositor, through each part's own visual, so once it starts it costs the UI thread
/// nothing per frame, and it holds up no input: a visual's opacity and translation change what is drawn, never where
/// the page is laid out, so a click during the entrance lands on what it would have anyway. A page
/// back from the navigation cache keeps its state and its scroll position, and plays only this.</para>
///
/// <para>Every page lays itself out as one panel whose first row, or first child, is its header, so
/// that is what is brought in first. A page laid out otherwise comes in whole, at the header's time.</para>
/// </summary>
internal sealed class PageArrival(IMotionPolicy motion)
{
    /// <summary>How far below its place a part of the page starts, in device-independent pixels.</summary>
    private const float Rise = 16;

    private CompositionEasingFunction? _ease;

    /// <summary>The entrance waiting for its page's first frame, if one is.</summary>
    private EventHandler<object>? _waiting;

    /// <summary>Bring <paramref name="page"/> in.</summary>
    public void Play(Page page)
    {
        // A page left before it was ever drawn is not brought in after all.
        if (_waiting is not null)
        {
            CompositionTarget.Rendering -= _waiting;
            _waiting = null;
        }

        var played = motion.For(MotionToken.Page);

        if (played.IsInstant)
        {
            return;
        }

        var parts = Parts(page, played);

        foreach (var (part, _) in parts)
        {
            Hold(part, played);
        }

        // Started on the first frame after the page is in the window rather than now, because a page
        // can take a few hundred milliseconds to build and lay out, and the entrance is shorter than
        // that. Until then each part is held where the entrance starts, unseen.
        _waiting = (_, _) =>
        {
            if (!page.IsLoaded)
            {
                return;
            }

            CompositionTarget.Rendering -= _waiting;
            _waiting = null;

            foreach (var (part, delay) in parts)
            {
                Enter(part, played, delay);
            }
        };

        CompositionTarget.Rendering += _waiting;
    }

    /// <summary>Each part of <paramref name="page"/> to bring in, and how far behind the header it starts.</summary>
    private static List<(UIElement Part, TimeSpan Delay)> Parts(Page page, Motion played)
    {
        if (Panel(page) is not { } panel)
        {
            return [(page, PageEntrance.Start(isHeader: true, played))];
        }

        var parts = new List<(UIElement, TimeSpan)>(panel.Children.Count);

        for (var i = 0; i < panel.Children.Count; i++)
        {
            var part = panel.Children[i];
            var isHeader = panel is Grid ? Grid.GetRow((FrameworkElement)part) == 0 : i == 0;

            parts.Add((part, PageEntrance.Start(isHeader, played)));
        }

        return parts;
    }

    /// <summary>
    /// The panel a page is laid out in: its content, or the content of the scroller its content is.
    /// </summary>
    private static Panel? Panel(Page page) => page.Content switch
    {
        Panel panel => panel,
        ScrollViewer { Content: Panel panel } => panel,
        _ => null,
    };

    /// <summary>
    /// Put <paramref name="part"/> where its entrance starts. In place when nothing travels, because
    /// a part held for an entrance that never played can come back with motion since turned off.
    /// </summary>
    private static void Hold(UIElement part, Motion played)
    {
        var visual = ElementCompositionPreview.GetElementVisual(part);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Opacity = 0;

        ElementCompositionPreview.SetIsTranslationEnabled(part, true);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", played.Travels ? new Vector3(0, Rise, 0) : Vector3.Zero);
    }

    private void Enter(UIElement part, Motion played, TimeSpan delay)
    {
        var visual = ElementCompositionPreview.GetElementVisual(part);
        var compositor = visual.Compositor;

        // The curve Motion eases by, so a page arrives as every other move here does.
        var ease = _ease ??= compositor.CreateCubicBezierEasingFunction(
            Motion.EaseControlPoints.First,
            Motion.EaseControlPoints.Second);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        Time(fade, played, delay);
        visual.StartAnimation(nameof(Visual.Opacity), fade);

        if (!played.Travels)
        {
            return;
        }

        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, Rise, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, ease);
        Time(rise, played, delay);
        visual.StartAnimation("Translation", rise);
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
