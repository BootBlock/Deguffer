using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests;

/// <summary>
/// How much of the service list was read is something the memory view tells its reader (§7.2). These
/// prove that only a read carried to its end, with every entry read, says the list was read, and that
/// every other way a call can go either reads on or says the list was read in part.
/// </summary>
public sealed class ServiceListingProgressTests
{
    [Theory]
    [InlineData(true, false, true, 12, ServiceListing.Listed)]
    [InlineData(false, true, true, 12, null)]
    [InlineData(false, true, true, 0, ServiceListing.ListedInPart)]
    [InlineData(false, false, false, 0, ServiceListing.ListedInPart)]
    [InlineData(true, false, false, 12, ServiceListing.ListedInPart)]
    [InlineData(false, true, false, 12, ServiceListing.ListedInPart)]
    public void EachWayACallEndsSaysHowMuchWasRead(
        bool finished, bool moreData, bool parsed, int returned, ServiceListing? expected) =>
        Assert.Equal(expected, ServiceListingProgress.After(finished, moreData, parsed, returned));
}
