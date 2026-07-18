using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
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
        ]);

        var findings = await planner.PlanAllAsync();

        Assert.Equal(["large", "medium", "small"], findings.Select(f => f.Provider.Id));
    }

    /// <summary>
    /// §5.5: never block on a complete scan. Each finding reaches the caller as it is produced, so
    /// the preview fills in rather than staying blank until the slowest provider finishes.
    /// </summary>
    [Fact]
    public async Task ReportsEachFindingAsItIsProducedRatherThanOnlyAtTheEnd()
    {
        var journal = new List<string>();
        var planner = new CleanupPlanner(
        [
            new StubProvider("first", bytes: 1_000, journal: journal),
            new StubProvider("second", bytes: 9_000, journal: journal),
        ]);

        var found = new ProgressRecorder<Finding>();

        var findings = await planner.PlanAllAsync(status: null, found, CancellationToken.None);

        // Reported in the order they were planned, not the order they are finally sorted into.
        Assert.Equal(["first", "second"], found.Reports.Select(f => f.Provider.Id));
        Assert.Equal(["second", "first"], findings.Select(f => f.Provider.Id));
    }

    [Fact]
    public async Task AnAbsentToolchainYieldsAFindingWithNoPlanRatherThanBeingDropped()
    {
        var planner = new CleanupPlanner(
            [new StubProvider("absent", bytes: 0, present: false)]);

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

    [Theory]
    [InlineData(SafetyTier.RegenerableWithCost)]
    [InlineData(SafetyTier.UserData)]
    [InlineData(SafetyTier.DoNotTouch)]
    public async Task RefusesToExecuteAnythingAboveTier1UntilTheConfirmationFlowExists(SafetyTier tier)
    {
        // Milestone 1 ships Tier 1 only. The guard is here so the first Tier 2/3 provider cannot
        // silently inherit a path that deletes without the extra confirmation §7 requires.
        var provider = new StubProvider("risky", bytes: 5_000, tier: tier);
        var planner = new CleanupPlanner([provider]);

        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() => planner.ExecuteAsync(findings));
        Assert.False(provider.WasExecuted);
    }

    [Fact]
    public void TheDefaultSetIsTheVerifiedTier1SourcesAndNothingAboveTier1()
    {
        var planner = CleanupPlanner.CreateDefault();

        Assert.Equal(
            ["nuget", "gradle", "npm", "vscode-cpptools", "uv"],
            planner.Providers.Select(p => p.Id));
        Assert.All(planner.Providers, p => Assert.Equal(SafetyTier.RegenerableCache, p.Tier));
    }

    [Fact]
    public async Task InvalidatesEveryProviderBeforeAnyOfThemPlans()
    {
        // Ordering matters: invalidating inside the planning loop would throw away the machine
        // snapshot the previous provider just paid for, since providers share collaborators.
        List<string> journal = [];
        var planner = new CleanupPlanner(
            [new StubProvider("a", 1, journal: journal), new StubProvider("b", 2, journal: journal)]);

        await planner.PlanAllAsync();

        Assert.Equal(["invalidate:a", "invalidate:b", "plan:a", "plan:b"], journal);
    }

    private sealed class StubProvider(
        string id,
        long bytes,
        bool present = true,
        SafetyTier tier = SafetyTier.RegenerableCache,
        List<string>? journal = null) : ICleanupProvider
    {
        public bool WasExecuted { get; private set; }

        public void InvalidateCaches() => journal?.Add($"invalidate:{id}");

        public string Id => id;

        public string Name => id;

        public SafetyTier Tier => tier;

        public string WhatHappensOnNextUse => "Nothing.";

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(present);

        public Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
        {
            journal?.Add($"plan:{id}");
            return Task.FromResult(NewPlan());
        }

        private CleanupPlan NewPlan() => new()
        {
            ProviderId = id,
            ProviderName = id,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = bytes == 0
                ? []
                : [new RunCommandStep("tool", "clear", "Clear") { Estimated = new ScanSize(bytes, bytes) }],
        };

        public Task<CleanupResult> ExecuteAsync(CleanupPlan plan, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            WasExecuted = true;
            return Task.FromResult(new CleanupResult { ProviderId = id, ProviderName = id });
        }

        public Task<VerificationResult> VerifyAsync(CleanupPlan plan, CancellationToken ct = default) =>
            Task.FromResult(new VerificationResult());
    }
}
