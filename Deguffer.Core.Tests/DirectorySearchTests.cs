using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How far a search descends, judged from a path rather than met on the way down. A fake index and
/// a caller that reached a directory some other way both ask this, so it has to stop where the two
/// routes stop.
/// </summary>
public sealed class DirectorySearchTests
{
    /// <summary>
    /// A handed-off directory is still reached, so one that carries a sought name is still an
    /// answer, and nothing below it is.
    /// </summary>
    [Fact]
    public void DoesNotReachBelowADirectoryItHandsOff()
    {
        var search = DirectorySearches.For("obj").HandingOff([["nested", "app"]]);

        Assert.True(search.Reaches(["nested", "app"]));
        Assert.False(search.Reaches(["nested", "app", "obj"]));
        Assert.True(search.Reaches(["nested", "other", "obj"]));
        Assert.True(search.Reaches(["nested", "App", "obj"]));
    }
}
