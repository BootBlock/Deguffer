using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The transform the map's picture is put on screen with, one inside another as the compositor nests
/// the visuals that carry them.
/// </summary>
public sealed class MapTransformTests
{
    private const double Precision = 1e-9;

    /// <summary>
    /// Composing two transforms has to put every point where applying one and then the other does,
    /// because the placements of the drawings and the names are composed this way and the compositor
    /// applies them one after the other. Each axis scaled apart, as a canvas stretched over a new
    /// size while a resize settles is: a composition that mixed the axes would agree with the
    /// compositor only when they happen to match.
    /// </summary>
    [Theory]
    [InlineData(1.5, 1.5, 0, 0, 0.75, 0.75, 0, 0)]
    [InlineData(2, 0.5, 30, -40, 1.25, 3, -7, 11)]
    [InlineData(0.25, 4, -12, 9, 6, 0.5, 100, -60)]
    public void ComposingIsApplyingOneAfterTheOther(
        double innerScaleX,
        double innerScaleY,
        double innerX,
        double innerY,
        double outerScaleX,
        double outerScaleY,
        double outerX,
        double outerY)
    {
        var inner = new MapTransform(innerScaleX, innerScaleY, innerX, innerY);
        var outer = new MapTransform(outerScaleX, outerScaleY, outerX, outerY);

        var composed = inner.Then(outer);

        foreach (var (x, y) in new[] { (0.0, 0.0), (13.0, -21.0), (480.0, 270.0) })
        {
            var (insideX, insideY) = inner.Apply(x, y);
            var (expectedX, expectedY) = outer.Apply(insideX, insideY);
            var (actualX, actualY) = composed.Apply(x, y);

            Assert.Equal(expectedX, actualX, Precision);
            Assert.Equal(expectedY, actualY, Precision);
        }
    }
}
