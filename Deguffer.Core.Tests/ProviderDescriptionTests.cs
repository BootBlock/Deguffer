using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// What every shipped row says about itself when the user asks "what is this?".
///
/// The subject is <see cref="CleanupPlanner.CreateDefault"/>'s own list rather than a hand-kept one,
/// so a provider added later is covered by these the moment it is registered.
/// </summary>
public class ProviderDescriptionTests
{
    private static IReadOnlyList<ICleanupProvider> Shipped => CleanupPlanner.CreateDefault().Providers;

    /// <summary>
    /// Every field is required by the compiler, so what is left to go wrong is a field filled in
    /// with nothing. A row that names no publisher and explains nothing still renders, and renders
    /// as an empty panel under a link promising an explanation.
    /// </summary>
    [Fact]
    public void EveryShippedProviderAnswersAllFourQuestions()
    {
        Assert.All(Shipped, provider =>
        {
            var description = provider.Description;

            Assert.False(string.IsNullOrWhiteSpace(description.Application), provider.Id);
            Assert.False(string.IsNullOrWhiteSpace(description.Publisher), provider.Id);
            Assert.False(string.IsNullOrWhiteSpace(description.Purpose), provider.Id);
            Assert.False(string.IsNullOrWhiteSpace(description.Recommendation), provider.Id);
        });
    }

    /// <summary>
    /// The failure this guards is a copy-and-paste, which is how twenty-four hand-written
    /// explanations go wrong: a provider inherits the paragraph belonging to the one above it and
    /// then describes somebody else's directory. Publishers repeat legitimately — Microsoft owns
    /// several of these — so it is the explanation itself that must be its own.
    /// </summary>
    [Fact]
    public void NoTwoProvidersShareAnExplanation()
    {
        var duplicates = Shipped
            .GroupBy(p => p.Description.Purpose, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(p => p.Id)));

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// §7 gives each row a sentence saying what the next use costs. This is the answer to a
    /// different question, and a provider that fills the explanation in with a second copy of the
    /// cost has left the first question unanswered.
    /// </summary>
    [Fact]
    public void AnExplanationIsNotTheCostSentenceRepeated()
    {
        Assert.All(Shipped, provider => Assert.NotEqual(
            provider.WhatHappensOnNextUse, provider.Description.Purpose, StringComparer.Ordinal));
    }

    /// <summary>
    /// The headline verdict is the tier's, and the four tiers must not read alike. A Tier 3 row
    /// carrying Tier 1's sentence tells somebody their Recycle Bin rebuilds itself, which is the
    /// understatement §3 exists to prevent — and one careless arm in the switch is all it takes.
    /// </summary>
    [Fact]
    public void EachTierAdvisesDifferently()
    {
        SafetyTier[] tiers =
        [
            SafetyTier.RegenerableCache,
            SafetyTier.RegenerableWithCost,
            SafetyTier.UserData,
            SafetyTier.DoNotTouch,
        ];

        var advice = tiers.Select(t => t.ToCleaningAdvice()).ToList();

        Assert.All(advice, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        Assert.Equal(tiers.Length, advice.Distinct(StringComparer.Ordinal).Count());
    }
}
