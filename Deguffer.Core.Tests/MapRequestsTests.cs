using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which of the tracker's reports the map believes. A report about a move the map has replaced would
/// put the old camera back, and every click is resolved through the camera the map believes (§7.1).
/// </summary>
public sealed class MapRequestsTests
{
    private static readonly MapViewport Target = MapViewport.Anchored(8, 0.3, 0.6, 0.5, 0.5);

    [Fact]
    public void OnlyTheLatestRequestIsBelieved()
    {
        var requests = new MapRequests();

        requests.Asked(1, Target);
        requests.Asked(2, MapViewport.Whole);

        Assert.False(requests.Reports(1), "a report of a replaced move was believed");
        Assert.True(requests.Reports(2));
        Assert.False(requests.Reports(0), "a hand was believed while the map's own move was the latest");
    }

    [Fact]
    public void AHandOnTheCameraReplacesEveryRequest()
    {
        var requests = new MapRequests();

        requests.Asked(3, Target);
        requests.Taken();

        Assert.True(requests.Reports(0));
        Assert.False(requests.Reports(3));
    }

    /// <summary>
    /// The tracker refuses a request while a hand holds the camera, and then it is the hand's reports
    /// that say where the camera is. A refusal of an older request changes nothing.
    /// </summary>
    [Fact]
    public void ARefusedRequestLeavesTheCameraToTheHand()
    {
        var requests = new MapRequests();

        requests.Asked(4, Target);
        requests.Asked(5, MapViewport.Whole);
        requests.Refused(4);

        Assert.True(requests.Reports(5));

        requests.Refused(5);

        Assert.True(requests.Reports(0));
    }

    /// <summary>
    /// Only a refusal of the request the camera was following leaves it where it was last reported,
    /// so only then does the map put back what it took the screen to show.
    /// </summary>
    [Fact]
    public void ARefusalSaysWhetherItWasTheRequestFollowed()
    {
        var requests = new MapRequests();

        requests.Asked(4, Target);
        requests.Asked(5, MapViewport.Whole);

        Assert.False(requests.Refused(4), "a refusal of a replaced request was taken as the camera's");
        Assert.True(requests.Refused(5), "a refusal of the request followed went unnoticed");
        Assert.False(requests.Refused(5), "a second refusal of the same request was taken as the camera's");
    }

    /// <summary>
    /// A press stops a glide where it is. The tracker reports a frame or more late, so the reports of
    /// the stopped glide already on their way are where the screen is, and dropping them would leave a
    /// right-click to pick from a frame the screen showed a moment earlier (§7.1).
    /// </summary>
    [Fact]
    public void AHaltedMoveIsBelievedUntilTheCameraRests()
    {
        var requests = new MapRequests();

        requests.Asked(9, Target);
        requests.Halted(10);

        Assert.True(requests.Reports(9), "the stopped glide's reports in flight were dropped");
        Assert.True(requests.Reports(10));

        requests.Settled();

        Assert.False(requests.Reports(9), "a stopped glide was still believed after the camera rested");
        Assert.True(requests.Reports(10));
    }

    /// <summary>
    /// A halted move stops where it is, so nothing snaps it to where it was going, and a request or a
    /// hand that follows the halt ends the stopped move's claim at once.
    /// </summary>
    [Fact]
    public void AHaltedMoveGoesNowhereAndIsReplacedByWhatFollows()
    {
        var requests = new MapRequests();
        var near = MapViewport.Seen(Target.Zoom, Target.Left + 1e-7, Target.Top);

        requests.Asked(11, Target);
        requests.Halted(12);

        Assert.Equal(near, requests.Where(near));

        requests.Asked(13, null);

        Assert.False(requests.Reports(11), "a stopped glide outlived a request made after the halt");

        requests.Halted(14);
        requests.Taken();

        Assert.False(requests.Reports(13), "a stopped request outlived a hand taking the camera");
    }

    /// <summary>
    /// A move that arrives comes to rest exactly where it was going, though the single-precision
    /// tracker reports it a rounding error away, so the drawing already made of that place is shown
    /// again rather than painted afresh.
    /// </summary>
    [Fact]
    public void AMoveThatArrivesRestsExactlyWhereItWasGoing()
    {
        var requests = new MapRequests();
        var reported = MapViewport.Seen(Target.Zoom * (1 + 1e-7), Target.Left + 1e-7, Target.Top - 1e-7);

        requests.Asked(6, Target);

        Assert.Equal(Target, requests.Rest(reported));
        Assert.Equal(Target, requests.Where(reported));
    }

    /// <summary>
    /// A move stopped short rests where it stopped, held inside the picture; on its way it is where the
    /// screen shows it, stretched or not.
    /// </summary>
    [Fact]
    public void AMoveStoppedShortRestsWhereItStopped()
    {
        var requests = new MapRequests();
        var shortOf = MapViewport.Seen(Target.Zoom, -0.01, Target.Top);

        requests.Asked(7, Target);

        Assert.Equal(shortOf, requests.Where(shortOf));
        Assert.Equal(shortOf.Held(), requests.Rest(shortOf));
        Assert.Equal(0, requests.Rest(shortOf).Left);
    }

    /// <summary>A fling or a hand goes wherever the tracker stops it, so nothing is snapped to.</summary>
    [Fact]
    public void AFlingRestsWhereTheTrackerStopsIt()
    {
        var requests = new MapRequests();
        var near = MapViewport.Seen(Target.Zoom, Target.Left + 1e-7, Target.Top);

        requests.Asked(8, null);

        Assert.Equal(near, requests.Rest(near));
    }
}
