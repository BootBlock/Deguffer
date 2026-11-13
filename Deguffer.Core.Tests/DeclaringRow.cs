using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// A row that does nothing but declare, from a declaration and a discovery each test writes.
/// </summary>
internal sealed class DeclaringRow(
    Func<IReadOnlyList<ToolRoot>> roots,
    Func<CancellationToken, Task<IReadOnlyList<ToolRoot>>>? discover = null)
    : ICleanupProvider
{
    public Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

    public DeclaringRow(IReadOnlyList<ToolRoot> roots, IReadOnlyList<ToolRoot>? discovered = null)
        : this(() => roots, discovered is null ? null : _ => Task.FromResult(discovered))
    {
    }

    public string Id => "declaring";

    public string Name => "Declaring";

    public SafetyTier Tier => SafetyTier.RegenerableCache;

    public StepGrain Grain => StepGrain.Parts;

    public string WhatHappensOnNextUse => "Nothing.";

    public ProviderBasis Basis => ProviderBasis.Observed;

    public ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.TemporaryFiles,
        Application = "A stub, standing in for a real toolchain.",
        Publisher = "Nobody.",
        Purpose = "Nothing. This provider exists only for this test.",
        Recommendation = "Nothing to recommend.",
    };

    public bool IsAwaitingSourceFolders => false;

    public IReadOnlyList<ToolRoot> ToolRoots => roots();

    public Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        discover?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<ToolRoot>>([]);

    public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

    public void InvalidateCaches()
    {
    }

    public PassHold HoldCaches() => PassHold.Nothing;

    public Task BeginPass(PassStart start) => Task.CompletedTask;

    public Task<CleanupPlan> PlanAsync(MinimumAge keep = default, IProgress<ScanSize>? counted = null, CancellationToken ct = default) =>
        throw new NotSupportedException("This stub exists only to carry a declaration.");

    public Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        RunReach? runReach = null,
        RunResidue? residue = null,
        IProgress<double>? progress = null,
        IProgress<CommandUnderway?>? underway = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This stub exists only to carry a declaration.");

    public Task<VerificationResult> VerifyAsync(
        CleanupPlan plan,
        RunReach? runReach = null,
        RunResidue? residue = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This stub exists only to carry a declaration.");
}
