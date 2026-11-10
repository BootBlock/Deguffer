using Deguffer.Core.Memory;
using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>The Memory page reads the machine only while the reader can see it.</summary>
public sealed class MemoryWatchTests
{
    [Fact]
    public void ASeenPageWatches() => Assert.True(MemoryWatch.Wanted(Sight.Seen));

    /// <summary>
    /// A page behind other windows is shown as far as the XAML host knows, and does not watch all the
    /// same, any more than one that has gone or is in a minimised window.
    /// </summary>
    [Theory]
    [InlineData(Sight.Covered)]
    [InlineData(Sight.Unseen)]
    public void APageNobodyCanSeeDoesNotWatch(Sight sight) => Assert.False(MemoryWatch.Wanted(sight));
}
