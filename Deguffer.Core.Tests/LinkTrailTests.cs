using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Back returns to where each link was, nearest first, and to nowhere the reader chose for themselves,
/// and each link is followed from wherever the window last went.
/// </summary>
public sealed class LinkTrailTests
{
    private enum Place
    {
        Memory,
        Startup,
        Explore,
        Storage,
    }

    [Fact]
    public void NothingFollowedLeadsNowhere()
    {
        var trail = new LinkTrail<Place>(Place.Storage);

        Assert.False(trail.CanGoBack);
        Assert.Null(trail.Back());
        Assert.Equal(Place.Storage, trail.Here);
    }

    [Fact]
    public void BackReturnsToWhereTheLinkWas()
    {
        var trail = new LinkTrail<Place>(Place.Memory);

        trail.Follow(Place.Startup);
        Assert.True(trail.CanGoBack);
        Assert.Equal(Place.Startup, trail.Here);

        Assert.Equal(Place.Memory, trail.Back());
        Assert.False(trail.CanGoBack);
        Assert.Equal(Place.Memory, trail.Here);
    }

    /// <summary>A link followed from a page a link opened is followed from there, so Back retraces both in turn.</summary>
    [Fact]
    public void LinksFollowedInTurnAreRetracedInTurn()
    {
        var trail = new LinkTrail<Place>(Place.Memory);

        trail.Follow(Place.Startup);
        trail.Follow(Place.Explore);

        Assert.Equal(Place.Startup, trail.Back());
        Assert.Equal(Place.Memory, trail.Back());
        Assert.Null(trail.Back());
    }

    [Fact]
    public void ALinkToThePageAlreadyShownIsNotRemembered()
    {
        var trail = new LinkTrail<Place>(Place.Startup);

        trail.Follow(Place.Startup);
        Assert.False(trail.CanGoBack);
    }

    [Fact]
    public void APageTheReaderChoseForgetsTheTrail()
    {
        var trail = new LinkTrail<Place>(Place.Memory);
        trail.Follow(Place.Startup);
        trail.Follow(Place.Explore);

        trail.Choose(Place.Storage);

        Assert.False(trail.CanGoBack);
        Assert.Null(trail.Back());
    }

    /// <summary>A rail choice moves where the window is, so the next link is followed from the page chosen.</summary>
    [Fact]
    public void ALinkAfterAChoiceIsFollowedFromThePageChosen()
    {
        var trail = new LinkTrail<Place>(Place.Storage);
        trail.Choose(Place.Memory);

        trail.Follow(Place.Startup);

        Assert.Equal(Place.Memory, trail.Back());
    }

    /// <summary>
    /// A link followed from the page Back returned to is followed from there, and not from the page the
    /// window left by going back.
    /// </summary>
    [Fact]
    public void ALinkAfterBackIsFollowedFromWhereBackLed()
    {
        var trail = new LinkTrail<Place>(Place.Memory);
        trail.Follow(Place.Startup);
        trail.Back();

        trail.Follow(Place.Startup);

        Assert.Equal(Place.Memory, trail.Back());
    }

    [Fact]
    public void OnlyTheNearestLinksAreKept()
    {
        var trail = new LinkTrail<Place>(Place.Explore);

        // The first link goes from Explore and every later one between Memory and Startup, so the
        // oldest is the only Explore on the trail, and it is the one a full trail forgets.
        for (var i = 0; i <= LinkTrail<Place>.Depth; i++)
        {
            trail.Follow(i % 2 == 0 ? Place.Memory : Place.Startup);
        }

        var returns = 0;

        while (trail.Back() is { } place)
        {
            Assert.NotEqual(Place.Explore, place);
            returns++;
        }

        Assert.Equal(LinkTrail<Place>.Depth, returns);
    }
}
