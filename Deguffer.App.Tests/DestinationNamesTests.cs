using Deguffer.App.Shell;

namespace Deguffer.App.Tests;

/// <summary>
/// The window is titled after the page it shows, from the name the page's header and its rail item
/// read, so Alt+Tab and the taskbar say which page is open.
/// </summary>
public sealed class DestinationNamesTests
{
    public static TheoryData<Destination, string> Titles => new()
    {
        { Destination.Storage, "Deguffer - Storage" },
        { Destination.Explore, "Deguffer - Explore" },
        { Destination.Duplicates, "Deguffer - Duplicates" },
        { Destination.Health, "Deguffer - Health" },
        { Destination.InstalledApps, "Deguffer - Installed apps" },
        { Destination.Startup, "Deguffer - Startup" },
        { Destination.Memory, "Deguffer - Memory" },
        { Destination.Settings, "Deguffer - Settings" },
        { Destination.About, "Deguffer - About" },
    };

    [Theory]
    [MemberData(nameof(Titles))]
    public void TheWindowIsTitledAfterThePageShown(Destination destination, string title) =>
        Assert.Equal(title, DestinationNames.WindowTitle(destination));

    [Fact]
    public void EveryDestinationHasATitleOfItsOwn()
    {
        var destinations = Enum.GetValues<Destination>();

        Assert.Equal(destinations.Length, Titles.Count);
        Assert.Equal(destinations.Length, destinations.Select(DestinationNames.WindowTitle).Distinct(StringComparer.Ordinal).Count());
    }
}
