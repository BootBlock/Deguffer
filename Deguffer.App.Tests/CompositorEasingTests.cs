using Deguffer.App.Shell;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;

namespace Deguffer.App.Tests;

/// <summary>
/// Every move the compositor plays eases by one curve, made once per compositor: a wheel notch, a key
/// press and a row's entrance each used to make their own.
/// </summary>
public sealed class CompositorEasingTests
{
    [Fact]
    public void TheEaseIsMotionsCurve()
    {
        var (first, second) = CompositionThread.Run(() =>
        {
            var ease = Assert.IsType<CubicBezierEasingFunction>(CompositionThread.Compositor.MotionEase());

            return (ease.ControlPoint1, ease.ControlPoint2);
        });

        Assert.Equal(Motion.EaseControlPoints.First, first);
        Assert.Equal(Motion.EaseControlPoints.Second, second);
    }

    /// <summary>
    /// Asked for again, and through a visual's compositor as the controls reach it, it is the same
    /// curve rather than another one.
    /// </summary>
    [Fact]
    public void TheEaseIsMadeOncePerCompositor()
    {
        var (first, again, throughVisual) = CompositionThread.Run(() =>
        {
            var compositor = CompositionThread.Compositor;
            var visual = compositor.CreateContainerVisual();

            return (compositor.MotionEase(), compositor.MotionEase(), visual.Compositor.MotionEase());
        });

        Assert.Same(first, again);
        Assert.Same(first, throughVisual);
    }
}
