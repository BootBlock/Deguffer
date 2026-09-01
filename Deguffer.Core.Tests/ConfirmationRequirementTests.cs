using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The §7 confirmation rule on its own, without a planner or a WinUI host — the decision is a Core
/// type precisely so it is provable here.
/// </summary>
public sealed class ConfirmationRequirementTests
{
    [Theory]
    [InlineData(SafetyTier.RegenerableCache, ConfirmationLevel.None)]
    [InlineData(SafetyTier.RegenerableWithCost, ConfirmationLevel.Acknowledgement)]
    [InlineData(SafetyTier.UserData, ConfirmationLevel.TypedPhrase)]
    [InlineData(SafetyTier.DoNotTouch, ConfirmationLevel.Refused)]
    public void EachTierDemandsItsOwnLevelOfDeliberation(SafetyTier tier, ConfirmationLevel expected)
    {
        Assert.Equal(expected, ConfirmationRequirement.For(PlanFor(tier)).Level);
    }

    [Fact]
    public void Tier1NeedsNoAnswerAtAll()
    {
        Assert.True(ConfirmationRequirement.For(PlanFor(SafetyTier.RegenerableCache)).IsSatisfiedBy([]));
    }

    [Fact]
    public void Tier2IsNotSatisfiedByAnEmptyAnswerSet()
    {
        Assert.False(ConfirmationRequirement.For(PlanFor(SafetyTier.RegenerableWithCost)).IsSatisfiedBy([]));
    }

    /// <summary>
    /// The phrase is the point of typing it. Accepting *any* text would make the Tier 3 gate a
    /// keystroke rather than a decision — and this is the direction that fails dangerously.
    /// </summary>
    [Theory]
    [InlineData("something else")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Android")]      // a prefix of the real phrase
    [InlineData("Android SDK!")] // near-miss
    public void Tier3RejectsAPhraseThatIsNotTheOneAsked(string typed)
    {
        var requirement = ConfirmationRequirement.For(PlanFor(SafetyTier.UserData));

        Assert.False(requirement.IsSatisfiedBy([new Confirmation("subject", typed)]));
    }

    [Fact]
    public void Tier3RejectsAnAcknowledgementCarryingNoPhrase()
    {
        var requirement = ConfirmationRequirement.For(PlanFor(SafetyTier.UserData));

        Assert.False(requirement.IsSatisfiedBy([new Confirmation("subject")]));
    }

    [Theory]
    [InlineData("Android SDK")]
    [InlineData("android sdk")]     // case is deliberation, not a password
    [InlineData("  Android SDK  ")] // surrounding whitespace is not a refusal
    public void Tier3AcceptsThePhraseAskedFor(string typed)
    {
        var requirement = ConfirmationRequirement.For(PlanFor(SafetyTier.UserData));

        Assert.Equal("Android SDK", requirement.RequiredPhrase);
        Assert.True(requirement.IsSatisfiedBy([new Confirmation("subject", typed)]));
    }

    /// <summary>§3 keeps Tier 4 out of the UI; no answer is an authorisation for it.</summary>
    [Fact]
    public void NothingSatisfiesTier4()
    {
        var requirement = ConfirmationRequirement.For(PlanFor(SafetyTier.DoNotTouch));

        Assert.Null(requirement.RequiredPhrase);
        Assert.False(requirement.IsSatisfiedBy([new Confirmation("subject", "Android SDK")]));
        Assert.False(requirement.IsSatisfiedBy([new Confirmation("subject")]));
        Assert.False(requirement.IsSatisfiedBy([]));
    }

    [Fact]
    public void AnAnswerForADifferentSubjectIsNotAnAnswer()
    {
        var requirement = ConfirmationRequirement.For(PlanFor(SafetyTier.RegenerableWithCost));

        Assert.False(requirement.IsSatisfiedBy([new Confirmation("a-different-provider")]));
        Assert.True(requirement.IsSatisfiedBy(
            [new Confirmation("a-different-provider"), new Confirmation("subject")]));
    }

    /// <summary>§7: the Tier 3 wording has to say plainly that the loss is permanent (§8 q4).</summary>
    [Fact]
    public void Tier3SaysPlainlyThatTheLossIsPermanent()
    {
        var consequence = ConfirmationRequirement.For(PlanFor(SafetyTier.UserData)).Consequence;

        Assert.Contains("permanent", consequence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be undone", consequence, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Only Tier 3 asks for typing; the others must not carry a phrase to render.</summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableCache)]
    [InlineData(SafetyTier.RegenerableWithCost)]
    [InlineData(SafetyTier.DoNotTouch)]
    public void OnlyTier3CarriesAPhrase(SafetyTier tier)
    {
        Assert.Null(ConfirmationRequirement.For(PlanFor(tier)).RequiredPhrase);
    }

    /// <summary>
    /// The shell stands its own blanket confirmation down whenever §7 will ask anyway. Getting this
    /// backwards for Tier 1 is the dangerous direction: it would suppress the only question asked
    /// before a deletion that §7 itself never prompts for.
    /// </summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableCache, false)]
    [InlineData(SafetyTier.RegenerableWithCost, true)]
    [InlineData(SafetyTier.UserData, true)]
    [InlineData(SafetyTier.DoNotTouch, true)]
    public void OnlyTier1PassesWithoutAQuestionOfItsOwn(SafetyTier tier, bool expected)
    {
        Assert.Equal(expected, ConfirmationRequirement.PromptsUser(WorkToDo(PlanFor(tier))));
    }

    /// <summary>
    /// An empty plan deletes nothing and is never asked about, so it must not be mistaken for a
    /// tier that §7 covers — otherwise selecting an already-clean Tier 2 row would silence the
    /// blanket confirmation for everything selected alongside it.
    /// </summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableCache)]
    [InlineData(SafetyTier.RegenerableWithCost)]
    [InlineData(SafetyTier.UserData)]
    public void AnEmptyPlanIsNeverAQuestion(SafetyTier tier)
    {
        Assert.False(ConfirmationRequirement.PromptsUser(PlanFor(tier)));
    }

    /// <summary>
    /// The mixed selection, which is where this went wrong in practice: one Tier 2 row alongside
    /// Tier 1 rows. The Tier 1 rows are not covered by §7, so they must come back here — a shell
    /// that concludes "§7 has this selection covered" deletes them having asked nothing at all,
    /// including when the user declines the single dialog they are shown.
    /// </summary>
    [Fact]
    public void Tier1RowsAlongsideATier2RowStillNeedAsking()
    {
        CleanupPlan[] selection =
        [
            WorkToDo(PlanFor(SafetyTier.RegenerableCache)) with { ProviderId = "uv" },
            WorkToDo(PlanFor(SafetyTier.RegenerableCache)) with { ProviderId = "npm" },
            WorkToDo(PlanFor(SafetyTier.RegenerableWithCost)) with { ProviderId = "platformio" },
        ];

        var unasked = ConfirmationRequirement.NotPromptedFor(selection, p => p);

        Assert.Equal(["uv", "npm"], unasked.Select(p => p.ProviderId));
    }

    /// <summary>The negative half: the Tier 2 row must not also be swept into the blanket ask.</summary>
    [Fact]
    public void ARowSection7AsksAboutIsNotAskedTwice()
    {
        CleanupPlan[] selection = [WorkToDo(PlanFor(SafetyTier.RegenerableWithCost))];

        Assert.Empty(ConfirmationRequirement.NotPromptedFor(selection, p => p));
    }

    /// <summary>An already-clear row deletes nothing, so it is not something to confirm.</summary>
    [Fact]
    public void AnAlreadyClearRowIsNotAskedAbout()
    {
        CleanupPlan[] selection = [PlanFor(SafetyTier.RegenerableCache)];

        Assert.Empty(ConfirmationRequirement.NotPromptedFor(selection, p => p));
    }

    /// <summary>
    /// The typed phrase is a preference, and switching it off retires Tier 3's own question. It does
    /// not promote the row to Tier 1: the level drops to <see cref="ConfirmationLevel.None"/> so that
    /// the shell's blanket confirmation picks the row up instead, which the pair of assertions
    /// further down proves.
    /// </summary>
    [Fact]
    public void Tier3AsksNothingOfItsOwnWhenTheTypedPhraseIsSwitchedOff()
    {
        var requirement = ConfirmationRequirement.For(PlanFor(SafetyTier.UserData), requireTypedPhrase: false);

        Assert.Equal(ConfirmationLevel.None, requirement.Level);
        Assert.Null(requirement.RequiredPhrase);
        Assert.True(requirement.IsSatisfiedBy([]));
    }

    /// <summary>
    /// The preference reaches Tier 3 and nothing else. Tier 4 in particular stays refused: a
    /// preference is not an authorisation, and that is the direction that loses data §3 never
    /// offered for deletion.
    /// </summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableCache, ConfirmationLevel.None)]
    [InlineData(SafetyTier.RegenerableWithCost, ConfirmationLevel.Acknowledgement)]
    [InlineData(SafetyTier.DoNotTouch, ConfirmationLevel.Refused)]
    public void SwitchingTheTypedPhraseOffLeavesEveryOtherTierAlone(SafetyTier tier, ConfirmationLevel expected)
    {
        Assert.Equal(expected, ConfirmationRequirement.For(PlanFor(tier), requireTypedPhrase: false).Level);
    }

    [Fact]
    public void NothingSatisfiesTier4EvenWithTheTypedPhraseSwitchedOff()
    {
        var requirement = ConfirmationRequirement.For(PlanFor(SafetyTier.DoNotTouch), requireTypedPhrase: false);

        Assert.False(requirement.IsSatisfiedBy([]));
        Assert.False(requirement.IsSatisfiedBy([new Confirmation("subject")]));
        Assert.False(requirement.IsSatisfiedBy([new Confirmation("subject", "Android SDK")]));
    }

    /// <summary>
    /// What is lost does not change because the user switched the typing off, so the wording must
    /// still say the deletion is permanent. With the preference off the blanket confirmation is the
    /// only dialog a Tier 3 row gets, and a Tier 1 sentence there would understate it.
    /// </summary>
    [Fact]
    public void Tier3StillSaysPlainlyThatTheLossIsPermanentWithTheTypedPhraseOff()
    {
        var consequence = ConfirmationRequirement
            .For(PlanFor(SafetyTier.UserData), requireTypedPhrase: false)
            .Consequence;

        Assert.Contains("permanent", consequence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be undone", consequence, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The half of the preference that has to hold for it to be safe at all. Switching the typed
    /// phrase off moves a Tier 3 row into the blanket confirmation; it must not drop out of both,
    /// which would delete user data having asked nothing whatsoever.
    /// </summary>
    [Fact]
    public void ATier3RowFallsToTheBlanketConfirmationWhenTheTypedPhraseIsOff()
    {
        CleanupPlan[] selection = [WorkToDo(PlanFor(SafetyTier.UserData))];

        Assert.False(ConfirmationRequirement.PromptsUser(selection[0], requireTypedPhrase: false));
        Assert.Equal(
            ["subject"],
            ConfirmationRequirement
                .NotPromptedFor(selection, p => p, requireTypedPhrase: false)
                .Select(p => p.ProviderId));
    }

    /// <summary>Tier 2 remains §7's to ask about, so it must not be swept in alongside it.</summary>
    [Fact]
    public void ATier2RowIsStillNotAskedTwiceWhenTheTypedPhraseIsOff()
    {
        CleanupPlan[] selection = [WorkToDo(PlanFor(SafetyTier.RegenerableWithCost))];

        Assert.Empty(ConfirmationRequirement.NotPromptedFor(selection, p => p, requireTypedPhrase: false));
    }

    private static CleanupPlan PlanFor(SafetyTier tier) => new()
    {
        ProviderId = "subject",
        ProviderName = "Android SDK",
        Tier = tier,
        WhatHappensOnNextUse = "Re-downloads on next build.",
    };

    private static CleanupPlan WorkToDo(CleanupPlan plan) => plan with
    {
        Steps = [new DeleteDirectoryStep(@"C:\Users\testuser\.cache\subject", "the cache")],
    };
}
