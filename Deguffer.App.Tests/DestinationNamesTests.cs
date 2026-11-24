using Deguffer.App.Shell;

namespace Deguffer.App.Tests;

/// <summary>
/// The title the window takes for each page names that page, from the name the page's header and its
/// rail item read, so Alt+Tab and the taskbar say which page is open. A WinUI window cannot be built
/// in a test process, so that the window takes this title as it navigates is observed by driving it.
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
    public void EachPagesWindowTitleNamesThePage(Destination destination, string title) =>
        Assert.Equal(title, DestinationNames.WindowTitle(destination));

    /// <summary>A collapsed rail item's tooltip is its only name, so it leads with the page's name.</summary>
    [Fact]
    public void ARailTooltipLeadsWithThePageNameThenItsQuestion() =>
        Assert.Equal(
            "Installed apps: remove entries for programs that are gone, or uninstall one",
            DestinationNames.RailTooltip(Destination.InstalledApps));

    /// <summary>
    /// A one-word label cannot say what its page is for, so every page's purpose is a question of its
    /// own rather than its name again, and no two pages share one.
    /// </summary>
    [Fact]
    public void EveryDestinationStatesAPurposeOfItsOwn()
    {
        var destinations = Enum.GetValues<Destination>();

        Assert.All(destinations, destination =>
        {
            var purpose = DestinationPurposes.Of(destination);

            Assert.False(string.IsNullOrWhiteSpace(purpose), $"{destination} has no purpose.");
            Assert.NotEqual(DestinationNames.Of(destination), purpose, StringComparer.OrdinalIgnoreCase);
            Assert.False(purpose.EndsWith('.'), $"{destination}'s purpose ends in a full stop, which help text does not.");
        });
        Assert.Equal(destinations.Length, destinations.Select(DestinationPurposes.Of).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryDestinationHasATitleOfItsOwn()
    {
        var destinations = Enum.GetValues<Destination>();

        Assert.Equal(destinations.Length, Titles.Count);
        Assert.Equal(destinations.Length, destinations.Select(DestinationNames.WindowTitle).Distinct(StringComparer.Ordinal).Count());
    }
}
