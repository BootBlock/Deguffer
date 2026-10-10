using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The spring a change of folder on the map flies on: it arrives within its motion's duration, never
/// passes its end from a standstill, and turned round part of the way it carries its speed into the
/// turn rather than stopping dead.
/// </summary>
public sealed class SpringTests
{
    private const double Precision = 1e-9;

    private static readonly TimeSpan Start = TimeSpan.FromSeconds(3);

    private static readonly Motion Flight = MotionToken.Entrance.Full;

    [Fact]
    public void FromAStandstillItArrivesAsTheDurationRunsOut()
    {
        var spring = Spring.At(0).Toward(1, Start, Flight);

        Assert.Equal(0, spring.ValueAt(Start), Precision);
        Assert.False(spring.IsOverAt(Start + (Flight.Duration * 0.8)), "it arrived well before its time");
        Assert.True(spring.IsOverAt(Start + Flight.Duration), "it had not arrived when its time ran out");
        Assert.Equal(1, spring.ValueAt(Start + Flight.Duration), 1e-3);
    }

    /// <summary>
    /// Critically damped: it closes on its end at every step and never passes it, so the picture it
    /// moves never shows the screen's edge bare on the way.
    /// </summary>
    [Fact]
    public void FromAStandstillItNeverPassesItsEnd()
    {
        var spring = Spring.At(1).Toward(0, Start, Flight);
        var previous = 1.0;

        for (var step = 1; step <= 100; step++)
        {
            var value = spring.ValueAt(Start + (Flight.Duration * (step / 50.0)));

            Assert.InRange(value, 0, previous);
            previous = value;
        }
    }

    /// <summary>
    /// Turned round part of the way, it goes on from where it was at the speed it had, so the picture
    /// does not jump and its speed changes smoothly: it carries on a little the way it was going
    /// before it comes back. An eased curve started again would set off from a standstill.
    /// </summary>
    [Fact]
    public void TurnedRoundItCarriesItsSpeedIntoTheTurn()
    {
        var turn = Start + (Flight.Duration * 0.2);
        var going = Spring.At(0).Toward(1, Start, Flight);
        var back = going.Toward(0, turn, Flight);

        Assert.Equal(going.ValueAt(turn), back.ValueAt(turn), Precision);
        Assert.Equal(going.SpeedAt(turn), back.SpeedAt(turn), Precision);
        Assert.True(going.SpeedAt(turn) > 0, "the spring was not moving when it was turned round");

        var justAfter = turn + TimeSpan.FromMilliseconds(4);

        Assert.True(back.ValueAt(justAfter) > back.ValueAt(turn), "it stopped dead at the turn");
        Assert.True(back.IsOverAt(turn + (Flight.Duration * 2)), "it never came back to rest");
        Assert.Equal(0, back.ValueAt(turn + (Flight.Duration * 2)), 1e-3);
    }

    [Fact]
    public void AMotionWithNoDurationIsThereAtOnce()
    {
        var spring = Spring.At(0).Toward(1, Start, Motion.Instant);

        Assert.True(spring.IsOverAt(Start));
        Assert.Equal(1, spring.ValueAt(Start));
        Assert.Equal(0, spring.SpeedAt(Start));
    }
}
