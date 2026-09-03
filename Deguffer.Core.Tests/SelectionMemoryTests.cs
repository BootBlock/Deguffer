using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a row starts ticked as once Deguffer remembers the last answer. The dangerous direction is
/// the one these tests are mostly about: a remembered tick is a pre-selection on the next scan, and
/// §7 leans on Tier 3 never being pre-selected as the last thing standing between the user and an
/// irreversible deletion when both confirmations are switched off.
/// </summary>
public class SelectionMemoryTests
{
    private const string Npm = "npm";
    private const string NodeModules = "node-modules";
    private const string CacheStep = @"C:\Users\testuser\AppData\Local\npm-cache\_cacache";
    private const string Fresh = @"C:\Users\testuser\src\gamma\node_modules";

    private static SelectionMemory Remembering(
        string providerId,
        bool isSelected,
        params (string Key, bool Selected)[] steps) =>
        new(new Dictionary<string, RememberedSelection>
        {
            [providerId] = new(isSelected, steps.ToDictionary(s => s.Key, s => s.Selected)),
        });

    [Fact]
    public void FallsBackToTheTierDefaultForAProviderItHasNeverSeen()
    {
        var memory = new SelectionMemory(new Dictionary<string, RememberedSelection>());

        Assert.True(memory.RowStartsSelected("gradle", SafetyTier.RegenerableCache, byDefault: true));
        Assert.False(memory.RowStartsSelected("maven", SafetyTier.RegenerableWithCost, byDefault: false));
    }

    /// <summary>
    /// The feature itself, in both directions. A Tier 1 row the user cleared stays cleared, and a
    /// Tier 2 row they chose comes back chosen — the second is the half that makes the setting worth
    /// having, since Tier 2 is the tier nothing pre-selects.
    /// </summary>
    [Fact]
    public void RestoresWhatTheUserChoseOverTheTierDefault()
    {
        Assert.False(Remembering(Npm, isSelected: false)
            .RowStartsSelected(Npm, SafetyTier.RegenerableCache, byDefault: true));

        Assert.True(Remembering("maven", isSelected: true)
            .RowStartsSelected("maven", SafetyTier.RegenerableWithCost, byDefault: false));
    }

    /// <summary>
    /// §3 and §7. The row was ticked when the user last looked at it, and it still arrives unticked,
    /// because a tick carried over from a previous session is exactly the pre-selection Tier 3 is
    /// never allowed.
    /// </summary>
    [Fact]
    public void NeverRestoresATickOnUserData()
    {
        var memory = Remembering(
            "recycle-bin",
            isSelected: true,
            (@"C:\$Recycle.Bin", true));

        Assert.False(memory.RowStartsSelected("recycle-bin", SafetyTier.UserData, byDefault: false));
        Assert.False(memory.StepStartsSelected(
            "recycle-bin", SafetyTier.UserData, @"C:\$Recycle.Bin", rowStartsSelected: true));
    }

    /// <summary>
    /// The reason the row alone is not enough. Restoring a ticked row by ticking its steps would
    /// re-select the one workspace the user picked out and left alone.
    /// </summary>
    [Fact]
    public void RestoresAnUntickedStepInsideATickedRow()
    {
        const string Kept = @"C:\Users\testuser\src\alpha\node_modules";
        const string Cleared = @"C:\Users\testuser\src\beta\node_modules";

        var memory = Remembering(NodeModules, isSelected: true, (Kept, true), (Cleared, false));

        // Tier 2 with the tier default against it, which is what the provider really is. Both
        // answers here are the user's own, so both have to beat that default.
        var row = memory.RowStartsSelected(NodeModules, SafetyTier.RegenerableWithCost, byDefault: false);

        Assert.True(row);
        Assert.True(memory.StepStartsSelected(NodeModules, SafetyTier.RegenerableWithCost, Kept, row));
        Assert.False(memory.StepStartsSelected(NodeModules, SafetyTier.RegenerableWithCost, Cleared, row));
    }

    /// <summary>
    /// A workspace that appeared since the last scan has no remembered answer, and the row is what
    /// holds it back. Tier 1 deliberately, so the tier default is <c>true</c> and only the cleared
    /// row can produce the expected answer.
    /// </summary>
    [Fact]
    public void LeavesAnUnseenStepClearInsideAClearedRow()
    {
        var memory = Remembering(NodeModules, isSelected: false);

        var row = memory.RowStartsSelected(NodeModules, SafetyTier.RegenerableCache, byDefault: true);

        Assert.False(row);
        Assert.False(memory.StepStartsSelected(NodeModules, SafetyTier.RegenerableCache, Fresh, row));
    }

    /// <summary>
    /// The other half, and the direction that widens. §3 offers Tier 2 without pre-selecting it,
    /// and a workspace cloned since the last scan is not a choice the user made — so the row's
    /// remembered tick does not reach it. Tier 1 is unaffected, because its default agrees.
    /// </summary>
    [Fact]
    public void LeavesAnUnseenStepClearInsideATickedTierTwoRow()
    {
        var memory = Remembering(NodeModules, isSelected: true);

        var tierTwo = memory.RowStartsSelected(NodeModules, SafetyTier.RegenerableWithCost, byDefault: false);
        var tierOne = memory.RowStartsSelected(NodeModules, SafetyTier.RegenerableCache, byDefault: true);

        Assert.True(tierTwo);
        Assert.False(memory.StepStartsSelected(NodeModules, SafetyTier.RegenerableWithCost, Fresh, tierTwo));

        Assert.True(tierOne);
        Assert.True(memory.StepStartsSelected(NodeModules, SafetyTier.RegenerableCache, Fresh, tierOne));
    }

    /// <summary>
    /// §7: "No preference reaches Tier 4, which stays excluded however the settings are left." No
    /// provider declares that tier today, so this guards the rule rather than a live path — and the
    /// code this class replaced could not have ticked such a row at all.
    /// </summary>
    [Fact]
    public void NeverRestoresATickOnADoNotTouchRow()
    {
        var memory = Remembering("hypothetical", isSelected: true, (Fresh, true));

        Assert.False(memory.RowStartsSelected("hypothetical", SafetyTier.DoNotTouch, byDefault: true));
        Assert.False(memory.StepStartsSelected("hypothetical", SafetyTier.DoNotTouch, Fresh, rowStartsSelected: true));
    }

    /// <summary>
    /// Step keys are mostly paths, and NTFS does not distinguish their case. A scan that reports one
    /// casing where the last reported another is describing the same directory, and matching them as
    /// different steps would restore a tick the user had cleared.
    /// </summary>
    [Fact]
    public void MatchesAStepWhoseCasingChangedBetweenScans()
    {
        var memory = Remembering(Npm, isSelected: true, (CacheStep, false));

        Assert.False(memory.StepStartsSelected(
            Npm, SafetyTier.RegenerableCache, CacheStep.ToUpperInvariant(), rowStartsSelected: true));
    }

    [Fact]
    public void ReplacesWhatWasRememberedAboutARowRatherThanMergingIntoIt()
    {
        const string Gone = @"C:\Users\testuser\src\alpha\node_modules";
        const string Current = @"C:\Users\testuser\src\beta\node_modules";

        var memory = Remembering(NodeModules, isSelected: true, (Gone, false));

        memory.Remember(NodeModules, new RememberedSelection(
            IsSelected: true,
            new Dictionary<string, bool> { [Current] = false }));

        // The vanished workspace is not carried forward, so the file tracks the machine rather than
        // every path Deguffer has ever planned.
        Assert.Equal([Current], memory.Entries[NodeModules].Steps.Keys);
    }
}
