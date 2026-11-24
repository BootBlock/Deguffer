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
    /// The typed phrase is a preference, and switching it off retires Tier 3's own question. It does
    /// not promote the row to Tier 1: the level drops to <see cref="ConfirmationLevel.None"/>, and the
    /// clean's one confirmation still lists the row with what it loses
    /// (<see cref="CleanConfirmationTests.ATier3RowWithTheTypedPhraseOffStillSaysItIsPermanent"/>).
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
    /// still say the deletion is permanent. This is the sentence the clean's confirmation states for a
    /// Tier 3 row once its typed box stops appearing, so keying it to the derived
    /// level rather than the tier would put a Tier 1 sentence in front of a permanent deletion.
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
    /// "Release" only of a plan in which nothing is destroyed, "Delete" only of a permanent loss, so the
    /// word carries weight where it appears, and "Clean" for everything else, a Tier 2 deletion included.
    /// A tier nobody decided falls to "Delete" rather than to the reassuring word.
    /// </summary>
    [Fact]
    public void DeleteIsSaidOnlyOfAPermanentLossAndReleaseOnlyWhereNothingIsDestroyed()
    {
        var release = new ReleaseLocalCopiesStep(@"C:\Users\testuser\OneDrive", "OneDrive", "Local copies");
        var plan = PlanFor(SafetyTier.RegenerableWithCost);
        var deletion = WorkToDo(plan);

        Assert.Equal(ConfirmationVerb.Release, ConfirmationRequirement.For(plan with { Steps = [release] }).Verb);
        Assert.Equal(ConfirmationVerb.Clean, ConfirmationRequirement.For(deletion).Verb);
        Assert.Equal(ConfirmationVerb.Clean, ConfirmationRequirement.For(deletion with { Steps = [.. deletion.Steps, release] }).Verb);
        Assert.Equal(ConfirmationVerb.Clean, ConfirmationRequirement.For(WorkToDo(PlanFor(SafetyTier.RegenerableCache))).Verb);
        Assert.Equal(ConfirmationVerb.Delete, ConfirmationRequirement.For(WorkToDo(PlanFor(SafetyTier.UserData))).Verb);
        Assert.Equal(ConfirmationVerb.Delete, ConfirmationRequirement.For(WorkToDo(PlanFor(SafetyTier.DoNotTouch))).Verb);
    }

    /// <summary>
    /// The instruction quotes the phrase the gate compares against, and the box's accessible name
    /// says it too, because a placeholder is no longer read once the user starts typing.
    /// </summary>
    [Fact]
    public void ATypedPhraseIsQuotedAboveTheBoxAndInItsName()
    {
        var typing = ConfirmationRequirement.For(PlanFor(SafetyTier.UserData)).Typing;

        Assert.NotNull(typing);
        Assert.Equal("Android SDK", typing.Phrase);
        Assert.Equal("Type “Android SDK” to confirm.", typing.Instruction);
        Assert.Equal("Type Android SDK to confirm deletion", typing.BoxName);
    }

    /// <summary>
    /// Where nothing is typed there is no box, so a row whose phrase the user switched off is not
    /// shown a box it could never fill.
    /// </summary>
    [Fact]
    public void NothingIsSaidAboutAPhraseNobodyTypes()
    {
        Assert.Null(ConfirmationRequirement.For(PlanFor(SafetyTier.RegenerableWithCost)).Typing);
        Assert.Null(ConfirmationRequirement.For(PlanFor(SafetyTier.UserData), requireTypedPhrase: false).Typing);
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
