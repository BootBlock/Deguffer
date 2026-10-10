using System.Numerics;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;

namespace Deguffer.App.Shell;

/// <summary>
/// The fades and moves a list's rows play on the compositor, and putting a row back at rest.
///
/// <para>Each plays through the container's own visual, so once it starts it costs the UI thread
/// nothing per frame, and it changes only where the row is drawn, never where it is laid out: a click
/// during one lands on the row that is there.</para>
/// </summary>
internal sealed class RowMoves
{
    /// <summary>How far to the left of its place an arriving row starts, in device-independent pixels.</summary>
    private const float Slide = 16;

    /// <summary>The containers a fade or a move has been played on, which are put back at rest when shown again.</summary>
    private readonly HashSet<SelectorItem> _animated = [];

    /// <summary>The glides under way, so a change during one starts from where its row is drawn.</summary>
    private readonly Dictionary<SelectorItem, (long Start, float From, Motion Played)> _gliding = [];

    private CompositionEasingFunction? _ease;

    /// <summary>How far below its place <paramref name="container"/> is drawn now, part of the way through a glide.</summary>
    public double Offset(SelectorItem container)
    {
        if (!_gliding.TryGetValue(container, out var glide))
        {
            return 0;
        }

        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - glide.Start);

        return glide.From * (1 - glide.Played.At(TimeSpan.Zero, elapsed));
    }

    /// <summary>Fade a row in where it is laid out.</summary>
    /// <param name="slides">Whether it slides in as it fades. One picture fading into the next only fades.</param>
    public void Arrive(SelectorItem container, Motion played, bool slides)
    {
        // Enabled whether or not this arrival slides, because putting the container back at rest
        // stops its translation, and stopping one that was never enabled is refused.
        _animated.Add(container);
        ElementCompositionPreview.SetIsTranslationEnabled(container, true);

        var visual = ElementCompositionPreview.GetElementVisual(container);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, Ease(visual.Compositor));
        fade.Duration = played.Duration;
        visual.StartAnimation(nameof(Visual.Opacity), fade);

        if (slides && played.Travels)
        {
            Translate(container, new Vector3(-Slide, 0, 0), played);
        }
    }

    /// <summary>Glide a row into its place from <paramref name="from"/> below it.</summary>
    public void Glide(SelectorItem container, float from, Motion played)
    {
        _animated.Add(container);
        ElementCompositionPreview.SetIsTranslationEnabled(container, true);
        Translate(container, new Vector3(0, from, 0), played);
        _gliding[container] = (Environment.TickCount64, from, played);
    }

    /// <summary>Put <paramref name="container"/> where it is laid out, drawn whole.</summary>
    public void Rest(SelectorItem container)
    {
        _gliding.Remove(container);

        // A container nothing was played on is at rest already, and has no translation to stop.
        if (!_animated.Remove(container))
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(container);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Opacity = 1;
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private void Translate(SelectorItem container, Vector3 from, Motion played)
    {
        var visual = ElementCompositionPreview.GetElementVisual(container);
        var move = visual.Compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, from);
        move.InsertKeyFrame(1, Vector3.Zero, Ease(visual.Compositor));
        move.Duration = played.Duration;
        visual.StartAnimation("Translation", move);
    }

    /// <summary>The curve every other move here eases by.</summary>
    private CompositionEasingFunction Ease(Compositor compositor) =>
        _ease ??= compositor.CreateCubicBezierEasingFunction(Motion.EaseControlPoints.First, Motion.EaseControlPoints.Second);
}
