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
        Assert.Equal(shortOf.Held, requests.Rest(shortOf));
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
