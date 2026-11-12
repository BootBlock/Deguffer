using Deguffer.Core.Native;

namespace Deguffer.Core.Tests;

public sealed class MultiStringTests
{
    [Fact]
    public void SplitsAtEachTerminatorAndStopsAtTheEmptyString() =>
        Assert.Equal(["first", "second"], MultiString.Split("first\0second\0\0tail".AsSpan()));

    [Fact]
    public void AnEmptyMultiStringHasNoStrings() =>
        Assert.Empty(MultiString.Split("\0".AsSpan()));

    /// <summary>A string property is one string and its terminator, and reads as a set of one.</summary>
    [Fact]
    public void ReadsASingleTerminatedStringAsOne() =>
        Assert.Equal(["oem12.inf"], MultiString.Split("oem12.inf\0".AsSpan()));
}
