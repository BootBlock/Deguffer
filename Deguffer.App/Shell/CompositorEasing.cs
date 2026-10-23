using System.Runtime.CompilerServices;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;

namespace Deguffer.App.Shell;

/// <summary>
/// The easing functions the compositor's animations are played through, each made once per
/// compositor rather than per animation or per control (G5).
///
/// <para>An easing function belongs to the compositor that made it and never changes once made, so
/// every animation on that compositor can share the one. Each is held weakly, so a compositor that
/// goes takes its functions with it.</para>
/// </summary>
internal static class CompositorEasing
{
    private static readonly ConditionalWeakTable<Compositor, CompositionEasingFunction> Motions = [];

    private static readonly ConditionalWeakTable<Compositor, CompositionEasingFunction> Linears = [];

    /// <summary>
    /// The curve every eased move on <paramref name="compositor"/> takes: <see cref="Motion.Ease"/>'s,
    /// as the cubic Bézier <see cref="Motion.EaseControlPoints"/> gives, so a move the compositor
    /// clocks lands as one the app clocks does.
    /// </summary>
    public static CompositionEasingFunction MotionEase(this Compositor compositor) =>
        Motions.GetValue(
            compositor,
            static owner => owner.CreateCubicBezierEasingFunction(Motion.EaseControlPoints.First, Motion.EaseControlPoints.Second));

    /// <summary>
    /// No easing at all on <paramref name="compositor"/>, for key frames whose own values already
    /// trace the curve, as a zoom's glide and the starfield's loop do.
    /// </summary>
    public static CompositionEasingFunction LinearEase(this Compositor compositor) =>
        Linears.GetValue(compositor, static owner => owner.CreateLinearEasingFunction());
}
