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

    private static CleanupPlan PlanFor(SafetyTier tier) => new()
    {
        ProviderId = "subject",
        ProviderName = "Android SDK",
        Tier = tier,
        WhatHappensOnNextUse = "Re-downloads on next build.",
    };
}
