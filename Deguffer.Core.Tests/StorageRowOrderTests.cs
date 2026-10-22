using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7 sorts the preview by size unless the user chose another order, and rows join it as their
/// providers finish.
/// </summary>
public sealed class StorageRowOrderTests
{
    [Fact]
    public void ALargerRowGoesAboveASmallerOne() =>
        Assert.Equal(1, StorageRowOrder.IndexFor([Row(900, 5), Row(100, 6)], Row(500, 0), StorageOrder.Size));

    [Fact]
    public void TheLargestRowLeadsAndTheSmallestTrails()
    {
        Assert.Equal(0, StorageRowOrder.IndexFor([Row(900, 0), Row(100, 1)], Row(1_000, 9), StorageOrder.Size));
        Assert.Equal(2, StorageRowOrder.IndexFor([Row(900, 8), Row(100, 9)], Row(50, 0), StorageOrder.Size));
        Assert.Equal(0, StorageRowOrder.IndexFor([], Row(50, 0), StorageOrder.Size));
    }

    /// <summary>
    /// Rows of one size stand in their providers' order, whichever finished first. Providers are
    /// planned several at once, so a tie broken by arrival would list the rows that found nothing in a
    /// different order after every scan.
    /// </summary>
    [Fact]
    public void ARowOfEqualSizeGoesByItsProvidersPlaceNotByWhenItArrived()
    {
        Assert.Equal(3, StorageRowOrder.IndexFor([Row(900, 0), Row(500, 1), Row(500, 2), Row(100, 3)], Row(500, 7), StorageOrder.Size));
        Assert.Equal(1, StorageRowOrder.IndexFor([Row(900, 0), Row(500, 4), Row(500, 6), Row(100, 3)], Row(500, 2), StorageOrder.Size));
        Assert.Equal(2, StorageRowOrder.IndexFor([Row(900, 0), Row(500, 4), Row(500, 6), Row(100, 3)], Row(500, 5), StorageOrder.Size));
    }

    /// <summary>
    /// A row shrunk by keeping an item stays where it is, so the list can be out of order. A newcomer
    /// goes before the first row that should follow it rather than wherever a search assuming order
    /// would land.
    /// </summary>
    [Fact]
    public void ANewcomerGoesBeforeTheFirstSmallerRowInAListOutOfOrder() =>
        Assert.Equal(1, StorageRowOrder.IndexFor([Row(900, 0), Row(10, 1), Row(800, 2)], Row(500, 3), StorageOrder.Size));

    /// <summary>
    /// By name, the size is ignored and case is not a rank: a lower-case name goes among the capitals
    /// rather than after all of them, as an ordinal comparison would put it.
    /// </summary>
    [Fact]
    public void ByNameARowGoesAmongTheOthersAlphabeticallyWhateverItsSizeOrCase()
    {
        OrderedRow[] listed = [Row(10, 0, "Gradle"), Row(900, 1, "NuGet"), Row(500, 2, "Yarn")];

        Assert.Equal(1, StorageRowOrder.IndexFor(listed, Row(1, 3, "npm"), StorageOrder.Name));
        Assert.Equal(0, StorageRowOrder.IndexFor(listed, Row(1, 3, "conda"), StorageOrder.Name));
        Assert.Equal(3, StorageRowOrder.IndexFor(listed, Row(9_999, 3, "Zig"), StorageOrder.Name));
    }

    [Fact]
    public void ByNameTwoRowsOfOneNameGoByTheirProvidersPlace() =>
        Assert.Equal(
            1,
            StorageRowOrder.IndexFor([Row(10, 0, "pip"), Row(10, 4, "pip")], Row(900, 2, "PIP"), StorageOrder.Name));

    /// <summary>Tier 1 leads, and within a tier the largest row does, as it does in the size order.</summary>
    [Fact]
    public void ByTierTheSaferTierLeadsAndTheLargestLeadsWithinIt()
    {
        OrderedRow[] listed =
        [
            Row(100, 0, tier: SafetyTier.RegenerableCache),
            Row(900, 1, tier: SafetyTier.RegenerableWithCost),
            Row(200, 2, tier: SafetyTier.RegenerableWithCost),
            Row(5_000, 3, tier: SafetyTier.UserData),
        ];

        Assert.Equal(0, StorageRowOrder.IndexFor(listed, Row(150, 9, tier: SafetyTier.RegenerableCache), StorageOrder.Tier));
        Assert.Equal(2, StorageRowOrder.IndexFor(listed, Row(500, 9, tier: SafetyTier.RegenerableWithCost), StorageOrder.Tier));
        Assert.Equal(3, StorageRowOrder.IndexFor(listed, Row(1, 9, tier: SafetyTier.RegenerableWithCost), StorageOrder.Tier));
    }

    [Fact]
    public void ByTierRowsOfOneTierAndSizeGoByTheirProvidersPlace() =>
        Assert.Equal(
            1,
            StorageRowOrder.IndexFor([Row(10, 0), Row(10, 4)], Row(10, 2), StorageOrder.Tier));

    /// <summary>
    /// The preferences file allows a number for an enum, so a hand-edited order arrives as a value no
    /// member names. It is read as the shipped order rather than one nothing can sort by.
    /// </summary>
    [Fact]
    public void AnOrderNoMemberNamesIsReadAsTheSizeOrder()
    {
        Assert.Equal(StorageOrder.Size, StorageRowOrder.Known((StorageOrder)9));
        Assert.Equal(StorageOrder.Name, StorageRowOrder.Known(StorageOrder.Name));
        Assert.Equal(1, StorageRowOrder.IndexFor([Row(900, 0, "a"), Row(100, 1, "b")], Row(500, 2, "z"), (StorageOrder)9));
    }

    private static OrderedRow Row(long size, int place, string name = "row", SafetyTier tier = SafetyTier.RegenerableCache) =>
        new(name, size, tier, place);
}
