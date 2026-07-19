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
    public async Task RefusesToExecuteAboveTier1WithoutTheConfirmationSection7Requires(SafetyTier tier)
    {
        // The failure that matters is a caller that simply forgot to ask: it must fail closed.
        var provider = new StubProvider("risky", bytes: 5_000, tier: tier);
        var planner = new CleanupPlanner([provider]);

        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(() => planner.ExecuteAsync(findings));
        Assert.False(provider.WasExecuted);
    }

    [Fact]
    public async Task ExecutesTier2OnceAcknowledged()
    {
        var provider = new StubProvider("android", bytes: 5_000, tier: SafetyTier.RegenerableWithCost);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        var results = await planner.ExecuteAsync(findings, [new Confirmation("android")]);

        Assert.Single(results);
        Assert.True(provider.WasExecuted);
    }

    /// <summary>
    /// A confirmation names its subject. Acknowledging one provider must not authorise a different
    /// one that happened to be selected in the same pass.
    /// </summary>
    [Fact]
    public async Task AConfirmationForAnotherProviderDoesNotAuthoriseThisOne()
    {
        var provider = new StubProvider("android", bytes: 5_000, tier: SafetyTier.RegenerableWithCost);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(
            () => planner.ExecuteAsync(findings, [new Confirmation("platformio")]));
        Assert.False(provider.WasExecuted);
    }

    /// <summary>§7: Tier 3 requires *typed* confirmation — a bare acknowledgement is not enough.</summary>
    [Fact]
    public async Task Tier3NeedsTheTypedPhraseNotMerelyAnAcknowledgement()
    {
        var provider = new StubProvider("workspace-state", bytes: 5_000, tier: SafetyTier.UserData);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(
            () => planner.ExecuteAsync(findings, [new Confirmation("workspace-state")]));
        Assert.False(provider.WasExecuted);

        // The StubProvider's Name is its id, so that is the phrase the requirement asks for.
        var results = await planner.ExecuteAsync(
            findings, [new Confirmation("workspace-state", "workspace-state")]);

        Assert.Single(results);
        Assert.True(provider.WasExecuted);
    }

    /// <summary>
    /// §3 excludes Tier 4 from the UI entirely, so no answer authorises it — including a correctly
    /// typed phrase, which is the route by which a Tier 4 row offered in error would get executed.
    /// </summary>
    [Fact]
    public async Task NoConfirmationAuthorisesTier4()
    {
        var provider = new StubProvider("credentials", bytes: 5_000, tier: SafetyTier.DoNotTouch);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(
            () => planner.ExecuteAsync(findings, [new Confirmation("credentials", "credentials")]));
        Assert.False(provider.WasExecuted);
    }

    /// <summary>Tier 1 is unaffected: selecting the row remains the whole decision.</summary>
    [Fact]
    public async Task Tier1StillExecutesWithNoConfirmationAtAll()
    {
        var provider = new StubProvider("npm", bytes: 5_000);
        var planner = new CleanupPlanner([provider]);

        var results = await planner.ExecuteAsync(await planner.PlanAllAsync());

        Assert.Single(results);
        Assert.True(provider.WasExecuted);
    }

    [Fact]
    public void TheDefaultSetIsTheVerifiedSourcesAndNothingAboveTier2()
    {
        var planner = CleanupPlanner.CreateDefault();

        Assert.Equal(
            ["dotnet-obj", "nuget", "gradle", "npm", "vscode-cpptools", "uv", "pip", "platformio", "playwright"],
            planner.Providers.Select(p => p.Id));

        // Tier 3 needs the typed-confirmation UI and a subject whose per-item attribution is
        // trustworthy; neither exists yet, so nothing above Tier 2 ships.
        Assert.All(planner.Providers, p =>
            Assert.True(p.Tier <= SafetyTier.RegenerableWithCost, $"{p.Id} is {p.Tier}"));
        // The Tier 2 members named individually, so demoting one to Tier 1 — which would make it
        // pre-selected and skip §7's acknowledgement — fails here rather than silently shipping.
        Assert.Equal(SafetyTier.RegenerableWithCost, planner.Providers.Single(p => p.Id == "platformio").Tier);
        Assert.Equal(SafetyTier.RegenerableWithCost, planner.Providers.Single(p => p.Id == "playwright").Tier);
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
