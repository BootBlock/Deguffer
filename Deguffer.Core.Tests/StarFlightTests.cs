using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The camera's loop through the About page's starfield: it ends where it began, so the compositor can
/// play it for ever with no seam, and it always flies forward.
/// </summary>
public sealed class StarFlightTests
{
    private const float Depth = 3000;

    private static readonly StarFlight Flight = new(TimeSpan.FromSeconds(40), Depth, speed: 620);

    /// <summary>
    /// Each star stands at the distance flown modulo the depth, so a loop that flew any other distance
    /// would jump every star at its seam.
    /// </summary>
    [Fact]
    public void ALoopFliesAWholeNumberOfDepths()
    {
        var samples = Flight.Samples(120);
        var flown = samples[^1].Travel - samples[0].Travel;

        Assert.Equal(Flight.LoopDistance, flown);
        Assert.True(flown >= Depth);
        Assert.Equal(0, flown % Depth, 0.01);
    }

    /// <summary>The mean speed is the nearest a seamless loop allows to the one asked for: 8 depths, not 620 × 40.</summary>
    [Fact]
    public void TheMeanSpeedIsRoundedToAWholeLoop() =>
        Assert.Equal(8 * Depth / 40, Flight.MeanSpeed, 0.001);

    /// <summary>
    /// The pose the loop ends on, worked out from the curves rather than copied, is the one it began
    /// on, so the camera does not snap when the compositor starts the loop again.
    /// </summary>
    [Fact]
    public void TheLoopEndsInThePoseItBeganIn()
    {
        var start = Flight.At(TimeSpan.Zero);
        var almost = Flight.At(Flight.Period - TimeSpan.FromMilliseconds(1));

        Assert.Equal(start.Bank, almost.Bank, 0.001);
        Assert.Equal(start.Yaw, almost.Yaw, 0.001);
        Assert.Equal(start.Pitch, almost.Pitch, 0.001);
        Assert.Equal(start.Streak, almost.Streak, 0.01);
        Assert.Equal(Flight.LoopDistance, almost.Travel - start.Travel, 1.0);
    }

    /// <summary>
    /// The camera never stops or flies backward, which would turn the stream round, and a star is
    /// always drawn longer in flight than at rest.
    /// </summary>
    [Fact]
    public void TheCameraAlwaysFliesForward()
    {
        var samples = Flight.Samples(400);

        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i].Travel > samples[i - 1].Travel, $"the camera flew backward at step {i}");
            Assert.True(samples[i].Streak > 1, $"a star stopped streaking at step {i}");
        }
    }

    /// <summary>The turns stay within their bounds, and each of them is used, so the flight banks and turns.</summary>
    [Fact]
    public void TheCameraBanksAndTurnsWithinItsBounds()
    {
        var samples = Flight.Samples(400);

        Assert.All(samples, pose =>
        {
            Assert.InRange(MathF.Abs(pose.Bank), 0, StarFlight.MaxBank + 1e-4f);
            Assert.InRange(MathF.Abs(pose.Yaw), 0, StarFlight.MaxYaw + 1e-4f);
            Assert.InRange(MathF.Abs(pose.Pitch), 0, StarFlight.MaxPitch + 1e-4f);
        });

        Assert.True(samples.Max(pose => pose.Bank) > StarFlight.MaxBank * 0.99f);
        Assert.True(samples.Max(pose => pose.Yaw) > StarFlight.MaxYaw * 0.99f);
        Assert.True(samples.Max(pose => pose.Pitch) > StarFlight.MaxPitch * 0.99f);
    }

    /// <summary>The still field is the loop's first pose with nothing streaking, because nothing is moving.</summary>
    [Fact]
    public void AtRestNoStarStreaks()
    {
        var rest = Flight.Rest;
        var start = Flight.At(TimeSpan.Zero);

        Assert.Equal(1, rest.Streak);
        Assert.Equal(start.Bank, rest.Bank);
        Assert.Equal(start.Yaw, rest.Yaw);
        Assert.True(start.Streak > 1);
    }
}
