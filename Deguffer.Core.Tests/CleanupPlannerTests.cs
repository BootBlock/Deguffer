using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

public class CleanupPlannerTests
{
    [Fact]
    public async Task SortsFindingsBySizeSoTheBiggestCauseLeads()
    {
        var planner = new CleanupPlanner(
        [
            new StubProvider("small", bytes: 1_000),
            new StubProvider("large", bytes: 9_000),
            new StubProvider("medium", bytes: 5_000),
        ]);

        var findings = await planner.PlanAllAsync();

        Assert.Equal(["large", "medium", "small"], findings.Select(f => f.Provider.Id));
    }

    [Fact]
    public async Task AnAbsentToolchainYieldsAFindingWithNoPlanRatherThanBeingDropped()
    {
        var planner = new CleanupPlanner([new StubProvider("absent", bytes: 0, present: false)]);

        var finding = Assert.Single(await planner.PlanAllAsync());

        Assert.False(finding.IsPresent);
        Assert.Null(finding.Plan);
        Assert.False(finding.HasReclaimableSpace);
    }

    [Fact]
    public async Task ExecuteSkipsFindingsWithNothingToDo()
    {
        var empty = new StubProvider("empty", bytes: 0);
        var planner = new CleanupPlanner([empty]);

        var results = await planner.ExecuteAsync(await planner.PlanAllAsync());

        Assert.Empty(results);
        Assert.False(empty.WasExecuted);
    }

    [Fact]
    public void TheDefaultSetIsTheThreeVerifiedTier1Sources()
    {
        var planner = CleanupPlanner.CreateDefault();

        Assert.Equal(["nuget", "gradle", "npm"], planner.Providers.Select(p => p.Id));
        Assert.All(planner.Providers, p => Assert.Equal(SafetyTier.RegenerableCache, p.Tier));
    }

    private sealed class StubProvider(string id, long bytes, bool present = true) : ICleanupProvider
    {
        public bool WasExecuted { get; private set; }

        public string Id => id;

        public string Name => id;

        public SafetyTier Tier => SafetyTier.RegenerableCache;

        public string WhatHappensOnNextUse => "Nothing.";

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(present);

        public Task<long> EstimateBytesAsync(CancellationToken ct = default) => Task.FromResult(bytes);

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
