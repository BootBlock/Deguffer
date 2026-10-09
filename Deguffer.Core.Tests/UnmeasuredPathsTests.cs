using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A plan whose measurement could not reach a path says so, on every provider, and its row is never
/// "Already clear" on that evidence.
///
/// <para>Asserted through a provider that plans exactly the targets it is handed, because the rule
/// belongs to <see cref="CleanupProviderBase.PlanAsync"/> rather than to any one tool. See
/// <see cref="UnmeasuredPaths"/>.</para>
/// </summary>
public sealed class UnmeasuredPathsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public UnmeasuredPathsTests() => _environment = new FakeUserEnvironment(_temp.CreateDirectory("profile"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ARefusedCacheIsNamedAndItsRowIsNotAlreadyClear()
    {
        _temp.CreateFile(1000, "tool", "cache", "a.bin");
        var cache = Path.Combine(_temp.Path, "tool", "cache");

        CleanupPlan plan;
        using (DeniedDirectory.WithUnreadableAttributes(cache))
        {
            plan = await new TargetsProvider(_environment, new DeletionTarget(cache, "A cache")).PlanAsync();
        }

        Assert.False(Assert.Single(plan.Steps).RemovesSomething);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Equal(UnreadableRoot.UnmeasuredNote(cache), Assert.Single(plan.Notes));
        Assert.Equal(FindingStatus.UnreadableRoot, Status(plan));
    }

    /// <summary>
    /// A folder that is there and would not be listed is certainly there, so it is told "could not
    /// list" rather than "Windows would not say what is here".
    /// </summary>
    [Fact]
    public async Task ACacheThatWouldNotBeListedIsToldItCouldNotBeListed()
    {
        _temp.CreateFile(1000, "cache", "a.bin");
        var cache = Path.Combine(_temp.Path, "cache");

        CleanupPlan plan;
        using (new DeniedDirectory(cache))
        {
            plan = await new TargetsProvider(_environment, new DeletionTarget(cache, "A cache")).PlanAsync();
        }

        Assert.Equal(UnreadableRoot.Note(cache), Assert.Single(plan.Notes));
        Assert.Equal(FindingStatus.UnreadableRoot, Status(plan));
    }

    /// <summary>
    /// The rest of the plan stands. The row still offers what was measured, and the note names the
    /// one folder that was not, so the total is read as short by an amount nobody can state.
    /// </summary>
    [Fact]
    public async Task TheRestOfThePlanStandsAndOnlyTheRefusedPathIsNamed()
    {
        _temp.CreateFile(1000, "tool", "cache", "a.bin");
        _temp.CreateFile(3000, "tool", "logs", "b.log");
        var cache = Path.Combine(_temp.Path, "tool", "cache");
        var logs = Path.Combine(_temp.Path, "tool", "logs");

        CleanupPlan plan;
        using (DeniedDirectory.WithUnreadableAttributes(cache))
        {
            plan = await new TargetsProvider(
                _environment, new DeletionTarget(cache, "A cache"), new DeletionTarget(logs, "Logs")).PlanAsync();
        }

        Assert.Equal(3000, plan.EstimatedBytes);
        Assert.Equal(UnreadableRoot.UnmeasuredNote(cache), Assert.Single(plan.Notes));
        Assert.True(plan.HasUnreadableRoot);
        Assert.Equal(FindingStatus.ReadyToClean, Status(plan));
    }

    /// <summary>The other direction: a plan that reached everything carries none of it.</summary>
    [Fact]
    public async Task APlanThatReachedEverythingIsUntouched()
    {
        _temp.CreateFile(1000, "cache", "a.bin");
        var cache = Path.Combine(_temp.Path, "cache");

        var plan = await new TargetsProvider(_environment, new DeletionTarget(cache, "A cache")).PlanAsync();

        Assert.False(plan.HasUnreadableRoot);
        Assert.Empty(plan.Notes);
    }

    private FindingStatus Status(CleanupPlan plan) =>
        new Finding(new TargetsProvider(_environment), IsPresent: true, plan).ToStatus(isElevated: false);

    /// <summary>
    /// A provider that plans exactly the targets it is handed, so what the base class makes of a
    /// measurement can be asserted without any one tool's rules in the way.
    /// </summary>
    private sealed class TargetsProvider(IUserEnvironment environment, params DeletionTarget[] targets)
        : CleanupProviderBase(environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, new FakeDirectoryScanner())
    {
        public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

        public override string Id => "targets";

        public override string Name => "Targets";

        public override SafetyTier Tier => SafetyTier.RegenerableCache;

        public override StepGrain Grain => StepGrain.Parts;

        public override string WhatHappensOnNextUse => "Nothing a test cares about.";

        public override ProviderDescription Description { get; } = new()
        {
            Application = "a test",
            Publisher = "the suite",
            Purpose = "Plans the targets it is handed.",
            Recommendation = "None.",
        };

        public override Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
        {
            var (steps, _) = await PlanDeletionsAsync(targets, keep, ct);

            return new CleanupPlan
            {
                ProviderId = Id,
                ProviderName = Name,
                Tier = Tier,
                WhatHappensOnNextUse = WhatHappensOnNextUse,
                Steps = steps,
            };
        }
    }
}
