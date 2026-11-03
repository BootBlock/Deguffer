using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A provider whose layout nobody here has seen is offered and never ticked for the user, whatever
/// its tier. The dangerous direction is a researched Tier 1 row arriving ticked, which is Deguffer
/// vouching for a folder it has only read about.
/// </summary>
public class ProviderBasisTests
{
    private const string Folder = @"C:\Users\testuser\AppData\Local\Example\Cache";
    private const string Fresh = @"C:\Users\testuser\AppData\Local\Example\Cache\Effects";

    private static FakeCleanupProvider Provider(ProviderBasis basis, SafetyTier tier = SafetyTier.RegenerableCache) =>
        new("example", tier) { Basis = basis };

    private static Finding FindingOf(FakeCleanupProvider provider) =>
        new(provider, IsPresent: true, new CleanupPlan
        {
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            Tier = provider.Tier,
            WhatHappensOnNextUse = provider.WhatHappensOnNextUse,
            Steps = [new DeleteDirectoryStep(Folder, "A cache") { Estimated = ScanSize.FromLengths(1024) }],
        });

    [Fact]
    public void AResearchedTierOneRowWithSomethingToRemoveStartsClear()
    {
        var finding = FindingOf(Provider(ProviderBasis.Researched));

        Assert.True(finding.HasSomethingToRemove);
        Assert.False(finding.IsPreSelectedByDefault);
    }

    /// <summary>The control: the same row, observed, is the Tier 1 default §3 describes.</summary>
    [Fact]
    public void AnObservedTierOneRowWithSomethingToRemoveStartsTicked() =>
        Assert.True(FindingOf(Provider(ProviderBasis.Observed)).IsPreSelectedByDefault);

    /// <summary>
    /// A tick the user gave is theirs, researched row or not. The basis decides the default, and
    /// never overrides an answer.
    /// </summary>
    [Fact]
    public void RestoresATickTheUserGaveAResearchedRow()
    {
        var provider = Provider(ProviderBasis.Researched);
        var memory = new SelectionMemory(new Dictionary<string, RememberedSelection>
        {
            [provider.Id] = new(IsSelected: true, new Dictionary<string, bool>()),
        });

        Assert.True(memory.RowStartsSelected(provider, byDefault: false));
    }

    /// <summary>
    /// The step rule's half. A folder that appeared since the user ticked a researched row is one
    /// they have never been shown, so it arrives clear, as one inside a ticked Tier 2 row does.
    /// </summary>
    [Fact]
    public void LeavesAnUnseenStepClearInsideATickedResearchedRow()
    {
        var researched = Provider(ProviderBasis.Researched);
        var observed = Provider(ProviderBasis.Observed);
        var memory = new SelectionMemory(new Dictionary<string, RememberedSelection>
        {
            [researched.Id] = new(IsSelected: true, new Dictionary<string, bool>()),
        });

        Assert.False(memory.StepStartsSelected(researched, Fresh, rowStartsSelected: true));
        Assert.True(memory.StepStartsSelected(observed, Fresh, rowStartsSelected: true));
    }

    [Theory]
    [InlineData(ProviderBasis.Observed, true)]
    [InlineData(ProviderBasis.Researched, false)]
    public void OnlyAnObservedBasisPermitsPreSelection(ProviderBasis basis, bool expected) =>
        Assert.Equal(expected, basis.PermitsPreSelection());

    /// <summary>The narrowing never widens: at a tier §3 never ticks, neither basis ticks the row.</summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableWithCost, ProviderBasis.Observed)]
    [InlineData(SafetyTier.RegenerableWithCost, ProviderBasis.Researched)]
    [InlineData(SafetyTier.UserData, ProviderBasis.Observed)]
    [InlineData(SafetyTier.UserData, ProviderBasis.Researched)]
    public void NeverTicksATierThatIsNotPreSelected(SafetyTier tier, ProviderBasis basis) =>
        Assert.False(Provider(basis, tier).IsPreSelectedByDefault());

    [Theory]
    [InlineData(ProviderBasis.Observed)]
    [InlineData(ProviderBasis.Researched)]
    public void EveryBasisHasAWordAndAnExplanation(ProviderBasis basis)
    {
        Assert.False(string.IsNullOrWhiteSpace(basis.ToDisplayName()));
        Assert.False(string.IsNullOrWhiteSpace(basis.ToExplanation()));
    }
}
