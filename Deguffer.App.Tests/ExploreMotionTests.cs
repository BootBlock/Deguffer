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

    /// <summary>The folder a flight through a folder goes to, in the picture of the folder passed through.</summary>
    private static readonly MapFrame Going = new(0.05, 0.6, 0.125, 0.125);

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

    /// <summary>
    /// A flight through a folder runs from the folder left filling the screen to the folder going to
    /// filling it, and reaches the middle picture once on the way, before the frame there moves
    /// anything: the map swaps the pictures over at that moment, so the frame has to be the way in's.
    /// </summary>
    [Fact]
    public void AFlightThroughAFolderPassesTheMiddleOnceAndArrivesAtTheFolderGoingTo()
    {
        var clock = new SteppedFrameClock();
        var (descent, arrived) = Descent(new ScriptedMotion(animationsEnabled: true), clock);
        var events = new List<string>();
        var leftAtMidway = (MapFrame?)null;
        var checkedMidway = false;

        descent.Midway += (_, _) =>
        {
            events.Add("midway");
            leftAtMidway = descent.Inner;
        };
        descent.Arrived += (_, _) => events.Add("arrived");

        descent.Pass(Shape, Going);

        Assert.Equal(MapFrame.Whole, descent.Inner);
        Assert.Equal(1, descent.Opacity);

        for (var elapsed = TimeSpan.Zero; elapsed < MotionToken.Entrance.Full.Duration * 2 && descent.IsMoving; elapsed += OneFrame)
        {
            clock.Step(OneFrame);

            if (events.Count == 1 && leftAtMidway is { } left)
            {
                // The frame that reached the middle is already on the way in: the folder going to is
                // small in its shape and only just coming in, and the folder left has shrunk away.
                // Still the way out's frame, the folder left shrunk almost to its shape, rather than
                // the way in's, which would have the folder going to near its own, far smaller one.
                Assert.InRange(left.Width, Going.Width * 2, 1 - Precision);
                Assert.True(descent.Inner.Width < Going.Width * 2, "the frame at the middle was not the way in's");
                Assert.True(descent.Opacity < 0.5);
                leftAtMidway = null;
                checkedMidway = true;
            }
        }

        Assert.True(checkedMidway, "the middle and the arrival came on the same frame");
        Assert.Equal(["midway", "arrived"], events);
        Assert.Equal(MapFrame.Whole, descent.Inner);
        Assert.Equal(1, descent.Opacity);
        Assert.Single(arrived);
    }

    /// <summary>A flight through a folder finished early passes the middle before it arrives, so the map has the folder going to drawn.</summary>
    [Fact]
    public void AFlightThroughAFolderFinishedEarlyPassesTheMiddleFirst()
    {
        var clock = new SteppedFrameClock();
        var (descent, _) = Descent(new ScriptedMotion(animationsEnabled: true), clock);
        var events = new List<string>();

        descent.Midway += (_, _) => events.Add("midway");
        descent.Arrived += (_, _) => events.Add("arrived");

        descent.Pass(Shape, Going);
        clock.Step(OneFrame);
        descent.Finish();

        Assert.Equal(["midway", "arrived"], events);
        Assert.Equal(MapFrame.Whole, descent.Inner);
    }

    /// <summary>
    /// Turned round on the way out, a flight through a folder goes back to the folder it left and never
    /// reaches the middle; turned round on the way in, it goes back only to the middle picture, the
    /// folder passed through, whose picture is the one the map has to go back to by then.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFlightThroughAFolderTurnedRoundGoesBackToWhereItsLegBegan(bool pastMidway)
    {
        var clock = new SteppedFrameClock();
        var (descent, arrived) = Descent(new ScriptedMotion(animationsEnabled: true), clock);
        var midways = 0;

        descent.Midway += (_, _) => midways++;
        descent.Pass(Shape, Going);
        clock.Step(OneFrame);

        while (pastMidway && midways == 0)
        {
            clock.Step(OneFrame);
        }

        Assert.Equal(pastMidway ? 1 : 0, midways);

        descent.Turn();

        for (var elapsed = TimeSpan.Zero; elapsed < MotionToken.Entrance.Full.Duration * 2 && descent.IsMoving; elapsed += OneFrame)
        {
            clock.Step(OneFrame);
        }

        Assert.False(descent.IsMoving);
        Assert.Single(arrived);
        Assert.Equal(pastMidway ? 1 : 0, midways);

        if (pastMidway)
        {
            AssertClose(Going, descent.Inner);
            Assert.Equal(MapFrame.Whole, descent.Outer);
            Assert.Equal(0, descent.Opacity);
        }
        else
        {
            Assert.Equal(MapFrame.Whole, descent.Inner);
            Assert.Equal(1, descent.Opacity);
        }
    }

    /// <summary>
    /// Turned round just short of the middle, the spring carries its speed on past it before it comes
    /// back. The flight still never reaches the middle: by then the map has gone back to the folder
    /// left and has no folder to pass through, so a middle reported there would swap in a picture
    /// nobody asked for.
    /// </summary>
    [Fact]
    public void AFlightThroughAFolderTurnedRoundJustShortOfTheMiddleNeverReachesIt()
    {
        var step = TimeSpan.FromMilliseconds(1);
        var reached = TimeSpan.Zero;

        {
            var clock = new SteppedFrameClock();
            var (probe, _) = Descent(new ScriptedMotion(animationsEnabled: true), clock);
            var midways = 0;

            probe.Midway += (_, _) => midways++;
            probe.Pass(Shape, Going);

            while (midways == 0)
            {
                clock.Step(step);
                reached += step;
            }
        }

        var turnClock = new SteppedFrameClock();
        var (descent, arrived) = Descent(new ScriptedMotion(animationsEnabled: true), turnClock);
        var reports = 0;

        descent.Midway += (_, _) => reports++;
        descent.Pass(Shape, Going);

        for (var elapsed = step; elapsed < reached - step; elapsed += step)
        {
            turnClock.Step(step);
        }

        Assert.Equal(0, reports);

        descent.Turn();

        for (var elapsed = TimeSpan.Zero; elapsed < MotionToken.Entrance.Full.Duration * 2 && descent.IsMoving; elapsed += step)
        {
            turnClock.Step(step);
        }

        Assert.Equal(0, reports);
        Assert.False(descent.IsMoving);
        Assert.Single(arrived);
        Assert.Equal(MapFrame.Whole, descent.Inner);
        Assert.Equal(1, descent.Opacity);
    }

    /// <summary>
    /// Turned round twice, a flight through a folder goes back the way it was going within its leg:
    /// on the way out, to the middle picture, the folder passed through, without going on past it; on
    /// the way in, to the folder going to again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFlightThroughAFolderTurnedRoundTwiceGoesOnWithinItsLeg(bool pastMidway)
    {
        var clock = new SteppedFrameClock();
        var (descent, arrived) = Descent(new ScriptedMotion(animationsEnabled: true), clock);
        var midways = 0;

        descent.Midway += (_, _) => midways++;
        descent.Pass(Shape, Going);
        clock.Step(OneFrame);

        while (pastMidway && midways == 0)
        {
            clock.Step(OneFrame);
        }

        descent.Turn();
        clock.Step(OneFrame);
        descent.Turn();

        for (var elapsed = TimeSpan.Zero; elapsed < MotionToken.Entrance.Full.Duration * 2 && descent.IsMoving; elapsed += OneFrame)
        {
            clock.Step(OneFrame);
        }

        Assert.False(descent.IsMoving);
        Assert.Single(arrived);
        Assert.Equal(pastMidway ? 1 : 0, midways);

        if (pastMidway)
        {
            Assert.Equal(MapFrame.Whole, descent.Inner);
            Assert.Equal(1, descent.Opacity);
        }
        else
        {
            // The middle picture fills the screen, and the folder left is back in its shape there.
            Assert.Equal(MapFrame.Whole, descent.Outer);
            AssertClose(Shape, descent.Inner);
            Assert.Equal(0, descent.Opacity);
        }
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
