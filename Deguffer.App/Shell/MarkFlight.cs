using System.Numerics;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Deguffer.App.Shell;

/// <summary>
/// The Deguffer mark flying in out of the depth to its place (<see cref="MotionToken.Mark"/>): it starts
/// as a speck at the middle of what it comes out of, the About page's starfield, and grows into place as
/// it comes. Where nothing travels it only fades in where it is.
///
/// <para>On the compositor, through the mark's own visual, so it costs the UI thread nothing per frame
/// and moves nothing in the layout.</para>
/// </summary>
internal static class MarkFlight
{
    /// <summary>How small the mark starts, as a share of its size: a speck, not nothing, so it is seen to come.</summary>
    private const float Speck = 0.02f;

    /// <summary>How far through the flight the mark is fully drawn, so it comes out of the depth rather than appearing.</summary>
    private const float Lit = 0.4f;

    /// <summary>Fly <paramref name="mark"/> in out of the middle of <paramref name="origin"/>, as <paramref name="played"/> plays.</summary>
    public static void Play(UIElement mark, UIElement origin, Motion played)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentNullException.ThrowIfNull(origin);

        if (played.IsInstant)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(mark);
        var compositor = visual.Compositor;
        var size = mark.ActualSize;
        var ease = compositor.CreateCubicBezierEasingFunction(Motion.EaseControlPoints.First, Motion.EaseControlPoints.Second);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(played.Travels ? Lit : 1, 1, ease);
        fade.Duration = played.Duration;
        visual.StartAnimation(nameof(Visual.Opacity), fade);

        if (!played.Travels)
        {
            return;
        }

        // From the middle of the origin to the mark's own middle, in the mark's terms, which Translation
        // is in.
        var middle = mark.TransformToVisual(origin).TransformPoint(new Windows.Foundation.Point(size.X / 2, size.Y / 2));
        var from = new Vector3((origin.ActualSize / 2) - new Vector2((float)middle.X, (float)middle.Y), 0);

        ElementCompositionPreview.SetIsTranslationEnabled(mark, true);
        visual.CenterPoint = new Vector3(size / 2, 0);

        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, from);
        move.InsertKeyFrame(1, Vector3.Zero, ease);
        move.Duration = played.Duration;
        visual.StartAnimation("Translation", move);

        var grow = compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector3(Speck, Speck, 1));
        grow.InsertKeyFrame(1, Vector3.One, ease);
        grow.Duration = played.Duration;
        visual.StartAnimation(nameof(Visual.Scale), grow);
    }
}
