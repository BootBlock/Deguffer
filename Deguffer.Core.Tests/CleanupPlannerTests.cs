using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

public sealed class CleanupPlannerTests
{
    [Fact]
    public async Task SortsFindingsBySizeSoTheBiggestCauseLeads()
    {
        var planner = new CleanupPlanner(
        [
            new StubProvider("small", bytes: 1_000),
            new StubProvider("large", bytes: 9_000),
            new StubProvider("medium", bytes: 5_000),
        ], FakeProcessInspector.NothingRunning);

        var findings = await planner.PlanAllAsync();

        Assert.Equal(["large", "medium", "small"], findings.Select(f => f.Provider.Id));
    }

    [Fact]
    public async Task AnAbsentToolchainYieldsAFindingWithNoPlanRatherThanBeingDropped()
    {
        var planner = new CleanupPlanner(
            [new StubProvider("absent", bytes: 0, present: false)], FakeProcessInspector.NothingRunning);

        var finding = Assert.Single(await planner.PlanAllAsync());

        Assert.False(finding.IsPresent);
        Assert.Null(finding.Plan);
        Assert.False(finding.HasReclaimableSpace);
    }

    [Fact]
    public async Task ExecuteSkipsFindingsWithNothingToDo()
    {
        var empty = new StubProvider("empty", bytes: 0);
        var planner = new CleanupPlanner([empty], FakeProcessInspector.NothingRunning);

        var results = await planner.ExecuteAsync(await planner.PlanAllAsync());

        Assert.Empty(results);
        Assert.False(empty.WasExecuted);
    }

    [Theory]
    [InlineData(SafetyTier.RegenerableWithCost)]
    [InlineData(SafetyTier.UserData)]
    [InlineData(SafetyTier.DoNotTouch)]
    public async Task RefusesToExecuteAnythingAboveTier1UntilTheConfirmationFlowExists(SafetyTier tier)
    {
        // Milestone 1 ships Tier 1 only. The guard is here so the first Tier 2/3 provider cannot
        // silently inherit a path that deletes without the extra confirmation §7 requires.
        var provider = new StubProvider("risky", bytes: 5_000, tier: tier);
        var planner = new CleanupPlanner([provider], FakeProcessInspector.NothingRunning);

        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() => planner.ExecuteAsync(findings));
        Assert.False(provider.WasExecuted);
    }

    [Fact]
    public async Task TakesOneProcessSnapshotPerPlanningPassRatherThanOnePerProvider()
    {
        var inspector = FakeProcessInspector.NothingRunning;
        var planner = new CleanupPlanner(
            [new StubProvider("a", 1), new StubProvider("b", 2), new StubProvider("c", 3)],
            inspector);

        await planner.PlanAllAsync();

        Assert.Equal(1, inspector.InvalidateCount);
    }

    [Fact]
    public void TheDefaultSetIsTheThreeVerifiedTier1Sources()
    {
        var planner = CleanupPlanner.CreateDefault();

        Assert.Equal(["nuget", "gradle", "npm"], planner.Providers.Select(p => p.Id));
        Assert.All(planner.Providers, p => Assert.Equal(SafetyTier.RegenerableCache, p.Tier));
    }

    private sealed class StubProvider(
        string id,
        long bytes,
        bool present = true,
        SafetyTier tier = SafetyTier.RegenerableCache) : ICleanupProvider
    {
        public bool WasExecuted { get; private set; }

        public string Id => id;

        public string Name => id;

        public SafetyTier Tier => tier;

        public string WhatHappensOnNextUse => "Nothing.";

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(present);

        public Task<CleanupPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(new CleanupPlan
        {
            ProviderId = id,
            ProviderName = id,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = bytes == 0 ? [] : [new RunCommandStep("tool", "clear", "Clear") { EstimatedBytes = bytes }],
        });

        public Task<CleanupResult> ExecuteAsync(CleanupPlan plan, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            WasExecuted = true;
            return Task.FromResult(new CleanupResult { ProviderId = id, ProviderName = id });
        }

        public Task<VerificationResult> VerifyAsync(CleanupPlan plan, CancellationToken ct = default) =>
            Task.FromResult(new VerificationResult());
    }
}
