using System.Runtime.CompilerServices;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;

namespace Deguffer.App.Shell;

/// <summary>
/// The curve every move the compositor plays eases by: <see cref="Motion.Ease"/>'s, as the cubic
/// Bézier <see cref="Motion.EaseControlPoints"/> gives, so a move the compositor clocks lands as one
/// the app clocks does.
///
/// <para>Made once per compositor rather than per move or per control (G5): an easing function
/// belongs to the compositor that made it, and is never changed once made, so every animation on that
/// compositor can share the one. Held weakly, so a compositor that goes takes its curve with it.</para>
/// </summary>
internal static class CompositorEasing
{
    private static readonly ConditionalWeakTable<Compositor, CompositionEasingFunction> Eases = [];

    /// <summary>The standard ease on <paramref name="compositor"/>, made the first time it is asked for.</summary>
    public static CompositionEasingFunction MotionEase(this Compositor compositor) =>
        Eases.GetValue(
            compositor,
            static owner => owner.CreateCubicBezierEasingFunction(Motion.EaseControlPoints.First, Motion.EaseControlPoints.Second));
}
