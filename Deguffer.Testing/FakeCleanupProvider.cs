using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// A provider whose plan the test states, for the rules that read a finding and for the shell that
/// holds findings together. What it plans is whatever <see cref="Steps"/> and
/// <see cref="ProtectedPaths"/> say at the moment it is asked, so a test can change them between a
/// scan and the re-plan after a clean.
///
/// <para>Its clean is real where it matters to §5.6: each directory step it is handed is removed from
/// disk, and nothing else is, so a test asserts what survived by looking at the tree it built. The
/// planner's own verifier then checks the plan's protected paths, as it does for every provider.</para>
/// </summary>
public sealed class FakeCleanupProvider(string id, SafetyTier tier = SafetyTier.RegenerableCache) : ICleanupProvider
{
    public string Id { get; } = id;

    public string Name { get; init; } = id;

    public SafetyTier Tier => tier;

    public StepGrain Grain { get; init; } = StepGrain.Items;

    public string WhatHappensOnNextUse => "Rebuilt on next use.";

    public ProviderDescription Description { get; } = new()
    {
        Application = "A fake, standing in for a real toolchain.",
        Publisher = "Nobody.",
        Purpose = "Nothing. This provider exists only for tests.",
        Recommendation = "Nothing to recommend.",
    };

    public bool IsPresent { get; set; } = true;

    public bool IsAwaitingSourceFolders { get; set; }

    /// <summary>What the next plan offers.</summary>
    public IReadOnlyList<CleanupStep> Steps { get; set; } = [];

    /// <summary>What the next plan promises will be standing afterwards (§5.6).</summary>
    public IReadOnlyList<ProtectedPath> ProtectedPaths { get; set; } = [];

    /// <summary>Thrown by the next plan instead of returning one: a provider failing, or a scan cancelled under it.</summary>
    public Exception? PlanFailure { get; set; }

    /// <summary>The fractions of its own work a clean reports, in order, as a removal of a large tree does.</summary>
    public IReadOnlyList<double> Fractions { get; set; } = [];

    /// <summary>
    /// Run once its clean has removed what it was handed: the machine changing as a clean changes it,
    /// such as the volume's free space moving, or what the next plan will find.
    /// </summary>
    public Action? AfterCleaning { get; set; }

    /// <summary>How many times it has been planned. A re-plan that left it alone did not add to this.</summary>
    public int PlanCount { get; private set; }

    /// <summary>Every plan it was handed to clean, in order, as the planner narrowed it.</summary>
    public List<CleanupPlan> Executed { get; } = [];

    public IReadOnlyList<ToolRoot> ToolRoots => [];

    public Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>([]);

    public void InvalidateCaches()
    {
    }

    public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(IsPresent);

    public Task<CleanupPlan> PlanAsync(MinimumAge keep = default, CancellationToken ct = default)
    {
        PlanCount++;

        return PlanFailure is { } failure
            ? Task.FromException<CleanupPlan>(failure)
            : Task.FromResult(new CleanupPlan
            {
                ProviderId = Id,
                ProviderName = Name,
                Tier = tier,
                WhatHappensOnNextUse = WhatHappensOnNextUse,
                Steps = Steps,
                ProtectedPaths = ProtectedPaths,
            });
    }

    public Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        RunReach? runReach = null,
        RunResidue? residue = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Executed.Add(plan);

        List<StepOutcome> outcomes = [];

        foreach (var step in plan.Steps.OfType<DeleteDirectoryStep>())
        {
            if (LongPath.DirectoryExists(step.Path))
            {
                Directory.Delete(LongPath.Extended(step.Path), recursive: true);
            }

            outcomes.Add(new StepOutcome(step.Description, Succeeded: true, step.EstimatedBytes, Refusals.None));
        }

        foreach (var fraction in Fractions)
        {
            progress?.Report(fraction);
        }

        AfterCleaning?.Invoke();

        return Task.FromResult(new CleanupResult
        {
            ProviderId = Id,
            ProviderName = Name,
            Steps = outcomes,
            Verification = PlanVerifier.Verify(plan, runReach, residue, ct),
        });
    }

    public Task<VerificationResult> VerifyAsync(
        CleanupPlan plan,
        RunReach? runReach = null,
        CancellationToken ct = default) =>
        Task.FromResult(PlanVerifier.Verify(plan, runReach, residue: null, ct));
}
