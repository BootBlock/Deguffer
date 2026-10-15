using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which providers the list of what to scan shows, and under which heading.
/// </summary>
public sealed class ProviderListingTests
{
    private static readonly FakeCleanupProvider Steam = new("steam") { Name = "Steam HTTP cache", Category = ProviderCategory.Games };
    private static readonly FakeCleanupProvider Epic = new("epic") { Name = "Epic Games launcher logs", Category = ProviderCategory.Games };
    private static readonly FakeCleanupProvider Temp = new("temp") { Name = "Temporary files", Category = ProviderCategory.TemporaryFiles };
    private static readonly FakeCleanupProvider Npm = new("npm") { Name = "npm package cache", Category = ProviderCategory.PackageCaches };

    private static readonly IReadOnlyList<ICleanupProvider> Providers = [Npm, Steam, Temp, Epic];

    /// <summary>
    /// The headings follow the category's order rather than the order the providers came in, so they
    /// stand still while the reader searches, and the rows under each are by name.
    /// </summary>
    [Fact]
    public void GroupsByCategoryInTheCategorysOrderAndSortsEachByName()
    {
        var groups = ProviderListing.Group(Providers, search: null);

        Assert.Equal(
            [ProviderCategory.TemporaryFiles, ProviderCategory.Games, ProviderCategory.PackageCaches],
            groups.Select(group => group.Category));
        Assert.Equal([Epic, Steam], groups[1].Providers);
    }

    [Fact]
    public void ASearchKeepsOnlyTheMatchingRowsAndDropsAnEmptyHeading()
    {
        var groups = ProviderListing.Group(Providers, "  CACHE ");

        Assert.Equal([ProviderCategory.Games, ProviderCategory.PackageCaches], groups.Select(group => group.Category));
        Assert.Equal([Steam], groups[0].Providers);
    }

    /// <summary>
    /// Each word may be anywhere, in any order, as in every other search box: "cache steam" finds
    /// "Steam HTTP cache", which never holds the phrase.
    /// </summary>
    [Fact]
    public void EveryWordMayBeAnywhereInAnyOrder()
    {
        var groups = ProviderListing.Group(Providers, "cache steam");

        Assert.Equal([Steam], Assert.Single(groups).Providers);
    }

    /// <summary>Typing the heading brings up everything under it, not just the rows that say the word.</summary>
    [Fact]
    public void ASearchMatchingAHeadingShowsTheWholeGroup()
    {
        // "Steam HTTP cache" does not say "games", and is listed because its heading does.
        var games = ProviderListing.Group(Providers, "games");

        Assert.Equal([Epic, Steam], Assert.Single(games).Providers);
    }

    [Fact]
    public void ASearchMatchesTheApplicationADescriptionNames()
    {
        // Every fake's description names "A fake, standing in for a real toolchain."
        var groups = ProviderListing.Group(Providers, "real toolchain");

        Assert.Equal(4, groups.Sum(group => group.Providers.Count));
    }

    /// <summary>
    /// Every shipped provider is listed exactly once, so nothing Deguffer can scan is missing from the
    /// list of what to scan.
    /// </summary>
    [Fact]
    public void ListsEveryShippedProviderOnce()
    {
        var shipped = CleanupPlanner.CreateDefault().Providers;

        var listed = ProviderListing.Group(shipped, search: null).SelectMany(group => group.Providers).ToList();

        Assert.Equal(shipped.Count, listed.Count);
        Assert.Equal(shipped.ToHashSet(), listed.ToHashSet());
    }
}
