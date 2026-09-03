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
    private const string CacheStep = @"C:\Users\testuser\AppData\Local\npm-cache\_cacache";

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
            "recycle-bin", SafetyTier.UserData, @"C:\$Recycle.Bin", byDefault: true));
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

        var memory = Remembering("node-modules", isSelected: true, (Kept, true), (Cleared, false));

        var row = memory.RowStartsSelected("node-modules", SafetyTier.RegenerableCache, byDefault: true);

        Assert.True(row);
        Assert.True(memory.StepStartsSelected("node-modules", SafetyTier.RegenerableCache, Kept, row));
        Assert.False(memory.StepStartsSelected("node-modules", SafetyTier.RegenerableCache, Cleared, row));
    }

    /// <summary>
    /// A workspace that appeared since the last scan has no remembered answer, so it follows the
    /// row. Falling back to the tier default instead would tick a new folder inside a row the user
    /// had cleared.
    /// </summary>
    [Fact]
    public void StartsAStepItHasNeverSeenWhereTheRowIs()
    {
        const string Fresh = @"C:\Users\testuser\src\gamma\node_modules";

        var memory = Remembering("node-modules", isSelected: false);

        var row = memory.RowStartsSelected("node-modules", SafetyTier.RegenerableCache, byDefault: true);

        Assert.False(row);
        Assert.False(memory.StepStartsSelected("node-modules", SafetyTier.RegenerableCache, Fresh, row));
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
            Npm, SafetyTier.RegenerableCache, CacheStep.ToUpperInvariant(), byDefault: true));
    }

    [Fact]
    public void ReplacesWhatWasRememberedAboutARowRatherThanMergingIntoIt()
    {
        const string Gone = @"C:\Users\testuser\src\alpha\node_modules";
        const string Current = @"C:\Users\testuser\src\beta\node_modules";

        var memory = Remembering("node-modules", isSelected: true, (Gone, false));

        memory.Remember("node-modules", new RememberedSelection(
            IsSelected: true,
            new Dictionary<string, bool> { [Current] = false }));

        // The vanished workspace is not carried forward, so the file tracks the machine rather than
        // every path Deguffer has ever planned.
        Assert.Equal([Current], memory.Entries["node-modules"].Steps.Keys);
    }

    /// <summary>
    /// A hand-edited entry that names no steps is well-formed JSON and reaches the record as a null.
    /// Every read of it has to survive that, or one stray edit crashes the preview.
    /// </summary>
    [Fact]
    public void ReadsAnEntryWhoseStepsAreMissingEntirely()
    {
        var memory = new SelectionMemory(new Dictionary<string, RememberedSelection>
        {
            [Npm] = new(IsSelected: false, Steps: null!),
        });

        Assert.False(memory.RowStartsSelected(Npm, SafetyTier.RegenerableCache, byDefault: true));
        Assert.True(memory.StepStartsSelected(Npm, SafetyTier.RegenerableCache, CacheStep, byDefault: true));
    }
}
