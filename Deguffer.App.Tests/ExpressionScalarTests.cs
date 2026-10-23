using System.Globalization;
using Deguffer.App.Controls;
using Microsoft.UI.Composition.Interactions;

namespace Deguffer.App.Tests;

/// <summary>
/// The camera's expressions are Core's rules worked over <see cref="ExpressionScalar"/>, so what they
/// say is Core's; what is the shell's is that the text is an expression the compositor reads, with
/// the rule's order kept.
/// </summary>
public sealed class ExpressionScalarTests
{
    /// <summary>
    /// A constant reads the same in every culture: one written with a comma would be read by the
    /// compositor as two arguments.
    /// </summary>
    [Fact]
    public void AConstantIsWrittenWithAPointWhateverTheCulture()
    {
        var before = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal("0.5", ExpressionScalar.Of(0.5).Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>An expression has no exponent, so a small constant is written out in full.</summary>
    [Fact]
    public void ASmallConstantIsWrittenWithoutAnExponent() =>
        Assert.Equal("0.000001", ExpressionScalar.Of(1e-6).Text);

    /// <summary>
    /// Every operation is bracketed, so a rule's order is kept whatever the compositor's precedence:
    /// here a subtraction under a division, which unbracketed would divide first.
    /// </summary>
    [Fact]
    public void AnOperationKeepsTheRulesOrder()
    {
        var a = new ExpressionScalar("a");
        var b = new ExpressionScalar("b");
        var c = new ExpressionScalar("c");

        Assert.Equal("((a - b) / c)", ((a - b) / c).Text);
        Assert.Equal("(-(a * b))", (-(a * b)).Text);
    }

    /// <summary>
    /// The compositor reads every expression the camera builds from Core's rules: one it cannot read is
    /// refused as the animation starts, and the picture would stay where it was.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheCompositorReadsTheCamerasExpressions(bool elastic) =>
        CompositionThread.Run(() =>
        {
            var compositor = CompositionThread.Compositor;
            var camera = new MapCamera(compositor);
            var tracker = InteractionTracker.Create(compositor);
            var frame = compositor.CreateRectangleGeometry();

            camera.Track(tracker, elastic);
            camera.Bound(tracker);

            var shown = compositor.CreateExpressionAnimation($"Vector2({MapCamera.ShownEdge("X")}, {MapCamera.ShownEdge("Y")})");
            shown.SetReferenceParameter("camera", camera.Properties);
            frame.StartAnimation(nameof(frame.Offset), shown);

            return true;
        });
}
