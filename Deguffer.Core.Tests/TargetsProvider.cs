using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>A provider that plans exactly the targets it is handed, measured by the given scanner.</summary>
internal sealed class TargetsProvider(IUserEnvironment environment, IDirectoryScanner scanner, params DeletionTarget[] targets)
    : CleanupProviderBase(environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner)
{
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

    /// <summary>
    /// A guard of the provider's own, carried on the plan as a provider with a floor carries one, while the
    /// targets are still measured under the guard the plan is asked with.
    /// </summary>
    public MinimumAge Floor { get; init; }

    public override string Id => "targets";

    public override string Name => "Targets";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse => "Nothing a test cares about.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.TemporaryFiles,
        Application = "a test",
        Publisher = "the suite",
        Purpose = "Plans the targets it is handed.",
        Recommendation = "None.",
    };

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct);

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            Notes = measured.Note is { } note ? [note] : [],
            Fallback = measured.Fallback,
            Keep = Floor,
        };
    }
}
