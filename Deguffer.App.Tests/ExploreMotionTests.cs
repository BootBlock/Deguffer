using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Tests;

/// <summary>
/// A change of folder on the map asks the motion policy how to play. The camera's own moves are the
/// compositor's, and their rules are Core's: see MapGlide, MapStretch and MapRequests.
/// A reader with Windows' Animation effects off gets no movement, and one who turns them off while
/// something is moving sees it land rather than play out.
/// </summary>
public sealed class ExploreMotionTests
{
    private static readonly MapFrame Shape = new(0.6, 0.1, 0.3, 0.2);

    private const double Precision = 1e-9;

    private static readonly TimeSpan OneFrame = TimeSpan.FromMilliseconds(16);

    [Fact]
    public void WithMotionOffAFolderFadesInWithoutGrowing()
    {
        var clock = new SteppedFrameClock();
        var (descent, arrived) = Descent(new ScriptedMotion(animationsEnabled: false), clock);
        var fade = MotionToken.Entrance.Reduced.Duration;

        descent.Start(Shape, FolderStep.Into);

        Assert.True(descent.IsMoving, "the folder cut to its drawing rather than fading in");
        Assert.Equal(0, descent.Opacity);

        clock.Step(fade / 2);

        Assert.True(descent.IsMoving);
        Assert.InRange(descent.Opacity, double.Epsilon, 1 - double.Epsilon);
        Assert.Empty(arrived);

        for (var elapsed = fade / 2; elapsed < fade; elapsed += OneFrame)
        {
            Assert.Equal(MapFrame.Whole, descent.Inner);
            Assert.Equal(MapFrame.Whole, descent.Outer);
            clock.Step(OneFrame);
        }

        Assert.Equal(MapFrame.Whole, descent.Inner);
        Assert.Equal(1, descent.Opacity);
        Assert.Single(arrived);
        Assert.False(clock.IsTicking);
    }

    [Fact]
    public void WithMotionOnAFolderGrowsOutOfItsShape()
    {
        var clock = new SteppedFrameClock();
        var (descent, arrived) = Descent(new ScriptedMotion(animationsEnabled: true), clock);

        descent.Start(Shape, FolderStep.Into);

        AssertClose(Shape, descent.Inner);
        Assert.Equal(0, descent.Opacity);

        clock.Step(MotionToken.Entrance.Full.Duration);

        Assert.Equal(MapFrame.Whole, descent.Inner);
        Assert.Equal(1, descent.Opacity);
        Assert.Single(arrived);
        Assert.False(clock.IsTicking);
    }

    /// <summary>A step out is the same flight the other way: the folder left shrinks back into its shape and goes.</summary>
    [Fact]
    public void WithMotionOnAFolderLeftShrinksIntoItsShape()
    {
        var clock = new SteppedFrameClock();
        var (descent, arrived) = Descent(new ScriptedMotion(animationsEnabled: true), clock);

        descent.Start(Shape, FolderStep.OutOf);

        Assert.Equal(MapFrame.Whole, descent.Inner);
        Assert.Equal(1, descent.Opacity);

        clock.Step(MotionToken.Entrance.Full.Duration);

        AssertClose(Shape, descent.Inner);
        Assert.Equal(MapFrame.Whole, descent.Outer);
        Assert.Equal(0, descent.Opacity);
        Assert.Single(arrived);
    }

    /// <summary>
    /// Turned round part of the way, the flight goes back from where it is without a jump, carries on
    /// a little the way it was going before it comes back, and arrives once, at the other end.
    /// </summary>
    [Fact]
    public void AFlightTurnedRoundGoesBackFromWhereItIs()
    {
        var clock = new SteppedFrameClock();
        var (descent, arrived) = Descent(new ScriptedMotion(animationsEnabled: true), clock);

        descent.Start(Shape, FolderStep.Into);
        clock.Step(OneFrame);
        clock.Step(OneFrame);

        var before = descent.Inner;

        descent.Turn();

        AssertClose(before, descent.Inner);

        clock.Step(TimeSpan.FromMilliseconds(4));

        Assert.True(descent.Inner.Width > before.Width, "the flight stopped dead as it was turned round");

        for (var elapsed = TimeSpan.Zero; elapsed < MotionToken.Entrance.Full.Duration * 2 && descent.IsMoving; elapsed += OneFrame)
        {
            clock.Step(OneFrame);
        }

        Assert.False(descent.IsMoving);
        AssertClose(Shape, descent.Inner);
        Assert.Equal(0, descent.Opacity);
        Assert.Single(arrived);
    }

    [Fact]
    public void AFlightWhenMotionIsTurnedOffArrivesAtTheNextFrame()
    {
        var motion = new ScriptedMotion(animationsEnabled: true);
        var clock = new SteppedFrameClock();
        var (descent, arrived) = Descent(motion, clock);

        descent.Start(Shape, FolderStep.Into);
        clock.Step(OneFrame);

        motion.AnimationsEnabled = false;
        clock.Step(OneFrame);

        Assert.False(descent.IsMoving);
        Assert.Equal(MapFrame.Whole, descent.Inner);
        Assert.Equal(1, descent.Opacity);
        Assert.Single(arrived);
        Assert.False(clock.IsTicking);
    }

    private static (ExploreDescent Descent, List<EventArgs> Arrived) Descent(ScriptedMotion motion, SteppedFrameClock clock)
    {
        var descent = new ExploreDescent(motion, clock);
        var arrived = new List<EventArgs>();

        descent.Arrived += (_, e) => arrived.Add(e);

        return (descent, arrived);
    }

    private static void AssertClose(MapFrame expected, MapFrame actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }
}
