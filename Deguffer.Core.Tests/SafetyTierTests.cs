using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>§3 — the tier table's "Default" column, asserted rather than assumed.</summary>
public class SafetyTierTests
{
    [Theory]
    [InlineData(SafetyTier.RegenerableCache, true)]
    [InlineData(SafetyTier.RegenerableWithCost, false)]
    [InlineData(SafetyTier.UserData, false)]
    [InlineData(SafetyTier.DoNotTouch, false)]
    public void OnlyTier1IsPreSelected(SafetyTier tier, bool expected) =>
        Assert.Equal(expected, tier.IsPreSelectedByDefault());

    [Theory]
    [InlineData(SafetyTier.RegenerableCache, true)]
    [InlineData(SafetyTier.RegenerableWithCost, true)]
    [InlineData(SafetyTier.UserData, true)]
    [InlineData(SafetyTier.DoNotTouch, false)]
    public void Tier4IsExcludedFromTheUiEntirely(SafetyTier tier, bool expected) =>
        Assert.Equal(expected, tier.IsOfferable());

    /// <summary>
    /// The badge on a Storage row and the reference list on the About page both read their prose
    /// from here, so an unexplained tier is an unexplained badge.
    /// </summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableCache)]
    [InlineData(SafetyTier.RegenerableWithCost)]
    [InlineData(SafetyTier.UserData)]
    [InlineData(SafetyTier.DoNotTouch)]
    public void EveryTierExplainsItselfInMoreThanItsLabel(SafetyTier tier)
    {
        var explanation = tier.ToExplanation();

        Assert.NotEqual(tier.ToString(), explanation);
        Assert.NotEqual(tier.ToDisplayName(), explanation);
        Assert.True(
            explanation.Length > tier.ToDisplayName().Length,
            $"{tier} explains itself in no more words than its label: '{explanation}'.");
    }

    /// <summary>No two tiers may share an explanation, or the badge stops telling them apart.</summary>
    [Fact]
    public void EachTierIsExplainedDifferently()
    {
        var explanations = Enum.GetValues<SafetyTier>().Select(tier => tier.ToExplanation()).ToList();

        Assert.Equal(explanations.Count, explanations.Distinct().Count());
    }

    /// <summary>
    /// §5.2 — Tier 4 is where an unrecognised child lands, and its explanation has to say so
    /// rather than reading as one more thing the tool might offer.
    /// </summary>
    [Fact]
    public void Tier4SaysItIsNeverOffered()
    {
        var explanation = SafetyTier.DoNotTouch.ToExplanation();

        Assert.Contains("Never offered", explanation);
        Assert.Contains("does not recognise", explanation);
    }

    [Fact]
    public void OnlyTier3IsIrreversibleLoss()
    {
        Assert.True(SafetyTier.UserData.IsIrreversibleLoss());

        Assert.All(
            new[] { SafetyTier.RegenerableCache, SafetyTier.RegenerableWithCost, SafetyTier.DoNotTouch },
            tier => Assert.False(tier.IsIrreversibleLoss()));
    }
}
