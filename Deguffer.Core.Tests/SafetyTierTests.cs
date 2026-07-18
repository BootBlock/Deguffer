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

    [Fact]
    public void OnlyTier3IsIrreversibleLoss()
    {
        Assert.True(SafetyTier.UserData.IsIrreversibleLoss());

        Assert.All(
            new[] { SafetyTier.RegenerableCache, SafetyTier.RegenerableWithCost, SafetyTier.DoNotTouch },
            tier => Assert.False(tier.IsIrreversibleLoss()));
    }
}
