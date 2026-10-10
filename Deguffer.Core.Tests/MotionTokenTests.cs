using System.Numerics;
using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What each kind of animation does for a reader with Windows' Animation effects on, and for one who
/// has turned them off: a camera move jumps, and an entrance fades in place.
/// </summary>
public sealed class MotionTokenTests
{
    [Fact]
    public void WithMotionOnEveryMoveTravels()
    {
        foreach (var token in new[] { MotionToken.Camera, MotionToken.Entrance, MotionToken.Page, MotionToken.Bar, MotionToken.Outcome, MotionToken.Starfield, MotionToken.Mark })
        {
            var motion = token.For(animationsEnabled: true);

            Assert.True(motion.Travels);
            Assert.False(motion.IsInstant);
        }
    }

    [Fact]
    public void WithMotionOffTheCameraJumps()
    {
        var motion = MotionToken.Camera.For(animationsEnabled: false);

        Assert.True(motion.IsInstant);
        Assert.False(motion.Travels);
    }

    [Fact]
    public void WithMotionOffAnEntranceFadesInPlace()
    {
        var motion = MotionToken.Entrance.For(animationsEnabled: false);

        Assert.False(motion.Travels);
        Assert.False(motion.IsInstant);
        Assert.True(motion.Duration < MotionToken.Entrance.Full.Duration);
    }

    [Fact]
    public void WithMotionOffAPageFadesInPlace()
    {
        var motion = MotionToken.Page.For(animationsEnabled: false);

        Assert.False(motion.Travels);
        Assert.False(motion.IsInstant);
        Assert.True(motion.Duration < MotionToken.Page.Full.Duration);
    }

    /// <summary>A bar stands at its figure at once with motion off: the figure beside it already says the value.</summary>
    [Fact]
    public void WithMotionOffABarIsAtItsFigureAtOnce()
    {
        var motion = MotionToken.Bar.For(animationsEnabled: false);

        Assert.True(motion.IsInstant);
        Assert.False(motion.Travels);
    }

    /// <summary>
    /// A clean's figures still fade in with motion off, so the reader sees that the run has finished,
    /// and they do not rise.
    /// </summary>
    [Fact]
    public void WithMotionOffACleansFiguresFadeInPlace()
    {
        var motion = MotionToken.Outcome.For(animationsEnabled: false);

        Assert.False(motion.Travels);
        Assert.False(motion.IsInstant);
        Assert.True(motion.Duration < MotionToken.Outcome.Full.Duration);
    }

    /// <summary>The About page's starfield is a still field with motion off: it neither flies nor flickers.</summary>
    [Fact]
    public void WithMotionOffTheStarfieldStandsStill()
    {
        var motion = MotionToken.Starfield.For(animationsEnabled: false);

        Assert.True(motion.IsInstant);
        Assert.False(motion.Travels);
    }

    /// <summary>With motion off the mark fades in where it settles in the header, and does not fly.</summary>
    [Fact]
    public void WithMotionOffTheMarkFadesInPlace()
    {
        var motion = MotionToken.Mark.For(animationsEnabled: false);

        Assert.False(motion.Travels);
        Assert.False(motion.IsInstant);
        Assert.True(motion.Duration < MotionToken.Mark.Full.Duration);
    }

    /// <summary>
    /// A change to how the whole window looks is never a cut, with motion on or off: a window that
    /// repaints at once is a flash.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheWindowNeverCutsBetweenLooks(bool animationsEnabled) =>
        Assert.False(MotionToken.Crossfade.For(animationsEnabled).IsInstant);

    /// <summary>
    /// Names a move uncovered fade in where they are, with motion on or off: they never fly in, and
    /// never flash on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewNamesOnTheMapFadeInPlace(bool animationsEnabled)
    {
        var motion = MotionToken.Detail.For(animationsEnabled);

        Assert.False(motion.Travels);
        Assert.False(motion.IsInstant);
    }

    /// <summary>
    /// A page's entrance, its header's and the rest's together, is over in under 300 ms, so it never
    /// stands between the reader and the page, and the rest of the page starts after its header.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APageIsInUnderThreeHundredMillisecondsHeaderFirst(bool animationsEnabled)
    {
        var motion = MotionToken.Page.For(animationsEnabled);
        var header = PageEntrance.Start(isHeader: true, motion);
        var rest = PageEntrance.Start(isHeader: false, motion);

        Assert.Equal(TimeSpan.Zero, header);
        Assert.InRange(rest, TimeSpan.FromTicks(1), motion.Duration);
        Assert.True(rest + motion.Duration < TimeSpan.FromMilliseconds(300));
    }

    /// <summary>
    /// An animation the compositor clocks has to move as one the app clocks does, or a page comes in
    /// on a different curve from every other move here. The compositor takes the curve as a Bézier,
    /// so the Bézier is checked against <see cref="Motion.At"/> along its length.
    /// </summary>
    [Fact]
    public void TheCompositorsCurveIsTheCurveAMotionEasesBy()
    {
        var motion = new Motion(TimeSpan.FromMilliseconds(1000), Travels: true);
        var (first, second) = Motion.EaseControlPoints;
        var start = TimeSpan.FromSeconds(3);

        for (var step = 0; step <= 20; step++)
        {
            var t = step / 20.0;
            var x = Bezier(t, first.X, second.X);
            var y = Bezier(t, first.Y, second.Y);

            Assert.Equal(y, motion.At(start, start + (motion.Duration * x)), 0.001);
        }

        // A cubic Bézier from 0 to 1 through two control values, at parameter t.
        static double Bezier(double t, double one, double two) =>
            (3 * Math.Pow(1 - t, 2) * t * one) + (3 * (1 - t) * t * t * two) + (t * t * t);
    }

    [Fact]
    public void AnInstantMotionIsAtItsEndFromTheStart()
    {
        var start = TimeSpan.FromSeconds(3);

        Assert.Equal(1, Motion.Instant.At(start, start));
    }

    [Fact]
    public void AMotionIsEasedOutAndEndsExactlyAtItsEnd()
    {
        var motion = new Motion(TimeSpan.FromMilliseconds(200), Travels: true);
        var start = TimeSpan.FromSeconds(3);

        Assert.Equal(0, motion.At(start, start));
        Assert.True(motion.At(start, start + (motion.Duration / 2)) > 0.5, "the motion was not eased out");
        Assert.Equal(1, motion.At(start, start + motion.Duration));
        Assert.Equal(1, motion.At(start, start + (motion.Duration * 4)));
    }
}
