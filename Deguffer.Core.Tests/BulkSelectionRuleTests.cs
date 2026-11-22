using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What each entry of the Storage page's selection menu does to one row. Only a row on screen is ever
/// ticked, a clear reaches every row, and "recommended" is §3's default column and nothing wider.
/// </summary>
public sealed class BulkSelectionRuleTests
{
    private static DeleteDirectoryStep Folder(long bytes, bool requiresElevation = false) =>
        new(@"C:\Users\testuser\AppData\Local\Fake\Cache", "Cached files")
        {
            Estimated = ScanSize.FromLengths(bytes),
            RequiresElevation = requiresElevation,
        };

    private static DeleteDirectoryStep EmptyFolders() =>
        Folder(0) with { IsLeftover = true, Estimated = new ScanSize(0, 0, Entries: 3) };

    private static Finding Offering(
        SafetyTier tier, ProviderBasis basis, params CleanupStep[] steps) =>
        new(new FakeCleanupProvider("fake", tier) { Basis = basis }, IsPresent: true, new CleanupPlan
        {
            ProviderId = "fake",
            ProviderName = "Fake",
            Tier = tier,
            WhatHappensOnNextUse = "Rebuilt.",
            Steps = steps,
        });

    private static Finding Cache(params CleanupStep[] steps) =>
        Offering(SafetyTier.RegenerableCache, ProviderBasis.Observed, steps);

    [Fact]
    public void AllTicksARowOnScreenWithBytesToGiveBack() =>
        Assert.Equal(RowTick.Tick, BulkSelectionRule.For(BulkSelection.All, Cache(Folder(10)), isListed: true, isTicked: false, isElevated: false));

    /// <summary>
    /// The reader asked for every row with a size, and a leftover of empty folders has none. It can
    /// still be ticked by hand, so it is left as it is rather than cleared.
    /// </summary>
    [Fact]
    public void AllLeavesARowOfEmptyFoldersAsItIs() =>
        Assert.Equal(RowTick.Leave, BulkSelectionRule.For(BulkSelection.All, Cache(EmptyFolders()), isListed: true, isTicked: false, isElevated: false));

    /// <summary>A filter hid the row, so a tick on it would add to the next clean something the reader cannot see.</summary>
    [Fact]
    public void AllLeavesARowAFilterHides() =>
        Assert.Equal(RowTick.Leave, BulkSelectionRule.For(BulkSelection.All, Cache(Folder(10)), isListed: false, isTicked: false, isElevated: false));

    /// <summary>
    /// The bytes are there, and none of them can be ticked by this process. The answer agrees with the
    /// row's disabled checkbox, and changes with the rights.
    /// </summary>
    [Fact]
    public void AllTicksBytesNeedingRightsOnlyWhereTheProcessHasThem()
    {
        var finding = Cache(Folder(10, requiresElevation: true));

        Assert.Equal(RowTick.Leave, BulkSelectionRule.For(BulkSelection.All, finding, isListed: true, isTicked: false, isElevated: false));
        Assert.Equal(RowTick.Tick, BulkSelectionRule.For(BulkSelection.All, finding, isListed: true, isTicked: false, isElevated: true));
    }

    /// <summary>
    /// A row with bytes it can give back and a step it cannot still has something to tick: the row's
    /// checkbox passes the disabled step over.
    /// </summary>
    [Fact]
    public void AllTicksARowWhereOnlySomeBytesNeedRights() =>
        Assert.Equal(
            RowTick.Tick,
            BulkSelectionRule.For(BulkSelection.All, Cache(Folder(10, requiresElevation: true), Folder(5)), isListed: true, isTicked: false, isElevated: false));

    /// <summary>"All" is the reader's own choice, which §3's "never pre-selected" does not govern.</summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableWithCost)]
    [InlineData(SafetyTier.UserData)]
    public void AllTicksEveryOfferedTier(SafetyTier tier) =>
        Assert.Equal(
            RowTick.Tick,
            BulkSelectionRule.For(BulkSelection.All, Offering(tier, ProviderBasis.Observed, Folder(10)), isListed: true, isTicked: false, isElevated: false));

    [Fact]
    public void RecommendedTicksAnObservedRegenerableCache() =>
        Assert.Equal(RowTick.Tick, BulkSelectionRule.For(BulkSelection.Recommended, Cache(Folder(10)), isListed: true, isTicked: false, isElevated: false));

    /// <summary>
    /// §3's default column: only Tier 1 starts ticked, and only where its layout was observed. Every
    /// other row is cleared, so the entry restores the first scan's selection rather than adding to
    /// whatever is ticked.
    /// </summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableWithCost, ProviderBasis.Observed)]
    [InlineData(SafetyTier.UserData, ProviderBasis.Observed)]
    [InlineData(SafetyTier.RegenerableCache, ProviderBasis.Researched)]
    public void RecommendedClearsEveryRowThatDoesNotStartTicked(SafetyTier tier, ProviderBasis basis) =>
        Assert.Equal(
            RowTick.Clear,
            BulkSelectionRule.For(BulkSelection.Recommended, Offering(tier, basis, Folder(10)), isListed: true, isTicked: false, isElevated: false));

    [Fact]
    public void RecommendedClearsARowAFilterHides() =>
        Assert.Equal(RowTick.Clear, BulkSelectionRule.For(BulkSelection.Recommended, Cache(Folder(10)), isListed: false, isTicked: false, isElevated: false));

    /// <summary>A clear removes less, so it reaches a hidden row as well as a shown one.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoneClearsEveryRow(bool isListed) =>
        Assert.Equal(RowTick.Clear, BulkSelectionRule.For(BulkSelection.None, Cache(Folder(10)), isListed, isTicked: true, isElevated: true));

    /// <summary>
    /// A row stays ticked while any item in it is, so a ticked row may hold items the reader unticked to
    /// keep. Neither entry that ticks reaches into it, or those items would be ticked back unseen and
    /// deleted by the next clean.
    /// </summary>
    [Theory]
    [InlineData(BulkSelection.All)]
    [InlineData(BulkSelection.Recommended)]
    public void AnEntryThatTicksLeavesARowThatAlreadyHasATick(BulkSelection selection) =>
        Assert.Equal(RowTick.Leave, BulkSelectionRule.For(selection, Cache(Folder(10)), isListed: true, isTicked: true, isElevated: false));

    /// <summary>A ticked row that is not recommended is still cleared: clearing removes less.</summary>
    [Fact]
    public void RecommendedClearsATickedRowThatDoesNotStartTicked() =>
        Assert.Equal(
            RowTick.Clear,
            BulkSelectionRule.For(BulkSelection.Recommended, Offering(SafetyTier.UserData, ProviderBasis.Observed, Folder(10)), isListed: true, isTicked: true, isElevated: false));

    [Fact]
    public void AnUnknownEntryIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BulkSelectionRule.For((BulkSelection)99, Cache(Folder(10)), isListed: true, isTicked: false, isElevated: false));
}
