using Deguffer.App.Shell;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;

namespace Deguffer.App.Tests;

/// <summary>
/// The compositor's easing functions are made once per compositor and shared, so a zoom's glide, a
/// wheel notch and a row's entrance take no new one each time they play.
/// </summary>
public sealed class CompositorEasingTests
{
    [Fact]
    public void TheMotionEaseIsMotionsCurve()
    {
        var (first, second) = CompositionThread.Run(() =>
        {
            var ease = Assert.IsType<CubicBezierEasingFunction>(CompositionThread.Compositor.MotionEase());

            return (ease.ControlPoint1, ease.ControlPoint2);
        });

        Assert.Equal(Motion.EaseControlPoints.First, first);
        Assert.Equal(Motion.EaseControlPoints.Second, second);
    }

    [Fact]
    public void TheLinearEaseEasesNothing() =>
        CompositionThread.Run(() => Assert.IsType<LinearEasingFunction>(CompositionThread.Compositor.LinearEase()));

    /// <summary>
    /// Asked for again, and through a visual's compositor as the controls reach it, each is the same
    /// function rather than another one.
    /// </summary>
    [Fact]
    public void EachEaseIsMadeOncePerCompositor()
    {
        var (motion, linear) = CompositionThread.Run(() =>
        {
            var compositor = CompositionThread.Compositor;
            var throughVisual = compositor.CreateContainerVisual().Compositor;

            return (
                (compositor.MotionEase(), compositor.MotionEase(), throughVisual.MotionEase()),
                (compositor.LinearEase(), compositor.LinearEase(), throughVisual.LinearEase()));
        });

        Assert.Same(motion.Item1, motion.Item2);
        Assert.Same(motion.Item1, motion.Item3);
        Assert.Same(linear.Item1, linear.Item2);
        Assert.Same(linear.Item1, linear.Item3);
    }
}
