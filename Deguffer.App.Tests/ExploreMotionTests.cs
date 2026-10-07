using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Tests;

/// <summary>
/// The map's two animations, the zoom glide and a folder opening, ask the motion policy how to play.
/// A reader with Windows' Animation effects off gets no movement, and one who turns them off while
/// something is moving sees it land rather than play out.
/// </summary>
public sealed class ExploreMotionTests
{
    private static readonly MapViewport Target = MapViewport.Anchored(8, 0.2, 0.3, 0.5, 0.5);

    private static readonly MapFrame Shape = new(0.6, 0.1, 0.3, 0.2);

    private const double Precision = 1e-9;

    private static readonly TimeSpan OneFrame = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// With motion off the picture is where it was asked to go the moment it is asked, with nothing in
    /// between, and is drawn there afresh once the wheel rests.
    /// </summary>
    [Fact]
    public void WithMotionOffAZoomJumpsAtOnce()
    {
        var clock = new SteppedFrameClock();
        var (zoom, moved, arrived) = Zoom(new ScriptedMotion(animationsEnabled: false), clock);

        zoom.GlideTo(Target);

        Assert.Equal(Target, zoom.Shown);
        Assert.Single(moved);
        Assert.Empty(arrived);

        clock.Step(ExploreZoom.JumpSettleTime);

        Assert.Equal(Target, zoom.Shown);
        Assert.Single(moved);
        Assert.Single(arrived);
        Assert.False(clock.IsTicking, "a zoom at rest went on waiting for frames");
    }

    /// <summary>
    /// A run of notches with motion off jumps at each one and is drawn once, after the last: drawing at
    /// every notch would put the map behind a touchpad that reports many a second.
    /// </summary>
    [Fact]
    public void WithMotionOffARunOfNotchesIsDrawnOnceTheWheelRests()
    {
        var clock = new SteppedFrameClock();
        var (zoom, moved, arrived) = Zoom(new ScriptedMotion(animationsEnabled: false), clock);
        var further = MapViewport.Anchored(16, 0.2, 0.3, 0.5, 0.5);

        zoom.GlideTo(Target);
        clock.Step(OneFrame);
        zoom.GlideTo(further);

        Assert.Equal(further, zoom.Shown);
        Assert.Equal(2, moved.Count);

        clock.Step(ExploreZoom.JumpSettleTime - OneFrame);

        Assert.Empty(arrived);

        clock.Step(OneFrame);

        Assert.Single(arrived);
        Assert.Equal(further, zoom.Shown);
    }

    [Fact]
    public void WithMotionOnAZoomMovesOverFramesAndThenArrives()
    {
        var clock = new SteppedFrameClock();
        var (zoom, moved, arrived) = Zoom(new ScriptedMotion(animationsEnabled: true), clock);

        zoom.GlideTo(Target);

        Assert.Equal(MapViewport.Whole, zoom.Shown);
        Assert.Empty(arrived);

        clock.Step(MotionToken.Camera.Full.Duration / 2);

        Assert.NotEqual(MapViewport.Whole, zoom.Shown);
        Assert.NotEqual(Target, zoom.Shown);
        Assert.Single(moved);
        Assert.Empty(arrived);

        clock.Step(MotionToken.Camera.Full.Duration);

        Assert.Equal(Target, zoom.Shown);
        Assert.Single(arrived);
        Assert.False(clock.IsTicking);
    }

    [Fact]
    public void AZoomMovingWhenMotionIsTurnedOffLandsAtTheNextFrame()
    {
        var motion = new ScriptedMotion(animationsEnabled: true);
        var clock = new SteppedFrameClock();
        var (zoom, _, arrived) = Zoom(motion, clock);

        zoom.GlideTo(Target);
        clock.Step(OneFrame);

        motion.AnimationsEnabled = false;
        clock.Step(OneFrame);

        Assert.Equal(Target, zoom.Shown);
        Assert.Single(arrived);
        Assert.False(clock.IsTicking);
    }

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

    private static (ExploreZoom Zoom, List<EventArgs> Moved, List<EventArgs> Arrived) Zoom(
        ScriptedMotion motion,
        SteppedFrameClock clock)
    {
        var zoom = new ExploreZoom(motion, clock);
        var moved = new List<EventArgs>();
        var arrived = new List<EventArgs>();

        zoom.Moved += (_, e) => moved.Add(e);
        zoom.Arrived += (_, e) => arrived.Add(e);

        return (zoom, moved, arrived);
    }

    private static (ExploreDescent Descent, List<EventArgs> Arrived) Descent(ScriptedMotion motion, SteppedFrameClock clock)
    {
        var descent = new ExploreDescent(motion, clock);
        var arrived = new List<EventArgs>();

        descent.Arrived += (_, e) => arrived.Add(e);

        return (descent, arrived);
    }
}
