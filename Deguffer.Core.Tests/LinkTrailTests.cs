using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Back returns to where each link was, nearest first, and to nowhere the reader chose for themselves.
/// </summary>
public sealed class LinkTrailTests
{
    private enum Place
    {
        Memory,
        Startup,
        Explore,
    }

    [Fact]
    public void NothingFollowedLeadsNowhere()
    {
        var trail = new LinkTrail<Place>();

        Assert.False(trail.CanGoBack);
        Assert.Null(trail.Back());
    }

    [Fact]
    public void BackReturnsToWhereTheLinkWas()
    {
        var trail = new LinkTrail<Place>();

        Assert.True(trail.Follow(Place.Memory, Place.Startup));
        Assert.True(trail.CanGoBack);

        Assert.Equal(Place.Memory, trail.Back());
        Assert.False(trail.CanGoBack);
    }

    [Fact]
    public void LinksAreRetracedNearestFirst()
    {
        var trail = new LinkTrail<Place>();

        trail.Follow(Place.Memory, Place.Startup);
        trail.Follow(Place.Startup, Place.Explore);

        Assert.Equal(Place.Startup, trail.Back());
        Assert.Equal(Place.Memory, trail.Back());
        Assert.Null(trail.Back());
    }

    [Fact]
    public void ALinkToThePageAlreadyShownIsNotRemembered()
    {
        var trail = new LinkTrail<Place>();

        Assert.False(trail.Follow(Place.Startup, Place.Startup));
        Assert.False(trail.CanGoBack);
    }

    [Fact]
    public void APageTheReaderChoseForgetsTheTrail()
    {
        var trail = new LinkTrail<Place>();
        trail.Follow(Place.Memory, Place.Startup);
        trail.Follow(Place.Startup, Place.Explore);

        Assert.True(trail.Forget());

        Assert.False(trail.CanGoBack);
        Assert.Null(trail.Back());
    }

    [Fact]
    public void ForgettingNothingChangesNothing() =>
        Assert.False(new LinkTrail<Place>().Forget());

    [Fact]
    public void OnlyTheNearestLinksAreKept()
    {
        var trail = new LinkTrail<Place>();

        // The first link goes from Explore and every later one from Memory or Startup, so the oldest is
        // the only Explore on the trail, and it is the one a full trail forgets.
        trail.Follow(Place.Explore, Place.Memory);

        for (var i = 0; i < LinkTrail<Place>.Depth; i++)
        {
            trail.Follow(i % 2 == 0 ? Place.Memory : Place.Startup, i % 2 == 0 ? Place.Startup : Place.Memory);
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
