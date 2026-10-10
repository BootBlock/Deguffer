using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Tests;

/// <summary>
/// A folder opening on the map asks the motion policy how to play. The camera's own moves are the
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

        descent.Start(Shape);

        Assert.True(descent.IsMoving, "the folder cut to its drawing rather than fading in");
        Assert.Equal(0, descent.Opacity);

        clock.Step(fade / 2);

        Assert.True(descent.IsMoving);
        Assert.InRange(descent.Opacity, double.Epsilon, 1 - double.Epsilon);
        Assert.Empty(arrived);

        for (var elapsed = fade / 2; elapsed < fade; elapsed += OneFrame)
        {
            Assert.Equal(MapFrame.Whole, descent.Opened);
            Assert.Equal(MapFrame.Whole, descent.Departing);
            clock.Step(OneFrame);
        }

        Assert.Equal(MapFrame.Whole, descent.Opened);
        Assert.Equal(1, descent.Opacity);
        Assert.Single(arrived);
        Assert.False(clock.IsTicking);
    }

    [Fact]
    public void WithMotionOnAFolderGrowsOutOfItsShape()
    {
        var clock = new SteppedFrameClock();
        var (descent, arrived) = Descent(new ScriptedMotion(animationsEnabled: true), clock);

        descent.Start(Shape);

        Assert.Equal(Shape.X, descent.Opened.X, Precision);
        Assert.Equal(Shape.Y, descent.Opened.Y, Precision);
        Assert.Equal(Shape.Width, descent.Opened.Width, Precision);
        Assert.Equal(Shape.Height, descent.Opened.Height, Precision);
        Assert.Equal(0, descent.Opacity);

        clock.Step(MotionToken.Entrance.Full.Duration);

        Assert.Equal(MapFrame.Whole, descent.Opened);
        Assert.Single(arrived);
    }

    [Fact]
    public void AFolderOpeningWhenMotionIsTurnedOffArrivesAtTheNextFrame()
    {
        var motion = new ScriptedMotion(animationsEnabled: true);
        var clock = new SteppedFrameClock();
        var (descent, arrived) = Descent(motion, clock);

        descent.Start(Shape);
        clock.Step(OneFrame);

        motion.AnimationsEnabled = false;
        clock.Step(OneFrame);

        Assert.False(descent.IsMoving);
        Assert.Equal(MapFrame.Whole, descent.Opened);
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
}
