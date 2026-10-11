using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The tiles a moving map keeps stay inside their budget of bytes, and the one let go to make room is
/// always the one least recently used.
/// </summary>
public sealed class MapTileCacheTests
{
    private readonly List<string> _evicted = [];

    /// <summary>Tiles of every size, added past the budget many times over, never hold more than it.</summary>
    [Fact]
    public void TheBudgetIsNeverExceeded()
    {
        var cache = new MapTileCache<string>(1000, _evicted.Add);
        var random = new Random(277);

        for (var i = 0; i < 500; i++)
        {
            cache.Add(Key(i), $"tile{i}", random.Next(1, 400));

            Assert.True(cache.Bytes <= cache.Budget, $"the cache holds {cache.Bytes} bytes of a budget of {cache.Budget}");
        }

        Assert.Equal(500, cache.Count + _evicted.Count);
    }

    /// <summary>
    /// The tile let go first is the one least recently used: added longest ago, unless it has been used
    /// since, in which case it is kept and the next oldest goes.
    /// </summary>
    [Fact]
    public void TheLeastRecentlyUsedTileGoesFirst()
    {
        var cache = new MapTileCache<string>(300, _evicted.Add);

        cache.Add(Key(1), "one", 100);
        cache.Add(Key(2), "two", 100);
        cache.Add(Key(3), "three", 100);

        Assert.True(cache.TryGet(Key(1), out var used));
        Assert.Equal("one", used);

        cache.Add(Key(4), "four", 100);
        cache.Add(Key(5), "five", 150);

        Assert.Equal(["two", "three", "one"], _evicted);
        Assert.False(cache.Contains(Key(1)));
        Assert.True(cache.Contains(Key(4)));
        Assert.True(cache.Contains(Key(5)));
        Assert.Equal(250, cache.Bytes);
    }

    /// <summary>Asking whether a tile is kept does not count as using it.</summary>
    [Fact]
    public void AskingWhetherATileIsKeptDoesNotKeepIt()
    {
        var cache = new MapTileCache<string>(200, _evicted.Add);

        cache.Add(Key(1), "one", 100);
        cache.Add(Key(2), "two", 100);

        Assert.True(cache.Contains(Key(1)));

        cache.Add(Key(3), "three", 100);

        Assert.Equal(["one"], _evicted);
    }

    /// <summary>A tile added again replaces the one kept for it, which is let go, and its bytes with it.</summary>
    [Fact]
    public void ATileAddedAgainReplacesTheOneKept()
    {
        var cache = new MapTileCache<string>(1000, _evicted.Add);

        cache.Add(Key(1), "old", 300);
        cache.Add(Key(1), "new", 200);

        Assert.Equal(["old"], _evicted);
        Assert.Equal(200, cache.Bytes);
        Assert.True(cache.TryGet(Key(1), out var kept));
        Assert.Equal("new", kept);
    }

    /// <summary>Cleared, the cache lets go of every tile and holds nothing.</summary>
    [Fact]
    public void ClearingLetsGoOfEveryTile()
    {
        var cache = new MapTileCache<string>(1000, _evicted.Add);

        cache.Add(Key(1), "one", 100);
        cache.Add(Key(2), "two", 100);
        cache.Clear();

        Assert.Equal(["one", "two"], _evicted);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }

    /// <summary>A tile larger than the whole budget is refused rather than kept over it.</summary>
    [Fact]
    public void ATileLargerThanTheBudgetIsRefused()
    {
        var cache = new MapTileCache<string>(100, _evicted.Add);

        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Add(Key(1), "huge", 101));
        Assert.Equal(0, cache.Bytes);
    }

    private static MapTileKey Key(int column) => new(Level: 3, Column: column, Row: 0);
}
