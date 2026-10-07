using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What each kind of animation does for a reader with Windows' Animation effects on, and for one who
/// has turned them off: a camera move jumps, and an entrance fades in place.
/// </summary>
public sealed class MotionTokenTests
{
    [Fact]
    public void WithMotionOnEveryTokenTravels()
    {
        foreach (var token in new[] { MotionToken.Camera, MotionToken.Entrance })
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
    public void AnInstantMotionIsAtItsEndFromTheStart()
    {
        var start = TimeSpan.FromSeconds(3);

        Assert.Equal(1, Motion.Instant.At(start, start));
        Assert.True(Motion.Instant.IsOverAt(start, start));
    }

    [Fact]
    public void AMotionIsEasedOutAndEndsExactlyAtItsEnd()
    {
        var motion = new Motion(TimeSpan.FromMilliseconds(200), Travels: true);
        var start = TimeSpan.FromSeconds(3);

        Assert.Equal(0, motion.At(start, start));
        Assert.True(motion.At(start, start + (motion.Duration / 2)) > 0.5, "the motion was not eased out");
        Assert.False(motion.IsOverAt(start, start + (motion.Duration / 2)));
        Assert.Equal(1, motion.At(start, start + motion.Duration));
        Assert.True(motion.IsOverAt(start, start + motion.Duration));
        Assert.Equal(1, motion.At(start, start + (motion.Duration * 4)));
    }
}
