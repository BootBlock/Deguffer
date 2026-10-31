using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The shapes of a change of view fade in over the view left, stay over the picture, and fade out over
/// the view arrived at, so neither end of the move cuts to them or from them.
/// </summary>
public sealed class MorphEnvelopeTests
{
    [Fact]
    public void TheShapesAreUnseenAsTheMoveStartsAndAsItLands()
    {
        Assert.Equal((0f, 0f), MorphEnvelope.Steps[0]);
        Assert.Equal((1f, 0f), MorphEnvelope.Steps[^1]);
    }

    [Fact]
    public void TheShapesAreFullyOverThePictureBetweenTheirFadeInAndTheirFadeOut()
    {
        Assert.Equal(
            [(0f, 0f), (MorphEnvelope.Over, 1f), (1 - MorphEnvelope.Over, 1f), (1f, 0f)],
            MorphEnvelope.Steps);
        Assert.InRange(MorphEnvelope.Over, 0.01f, 0.5f);
    }

    [Fact]
    public void TheStepsRunInOrderThroughTheMove()
    {
        for (var at = 1; at < MorphEnvelope.Steps.Count; at++)
        {
            Assert.True(MorphEnvelope.Steps[at].Progress > MorphEnvelope.Steps[at - 1].Progress);
        }
    }
}
