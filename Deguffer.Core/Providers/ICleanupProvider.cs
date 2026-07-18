using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One known cache source (§6.2). Adding support for a new cache is one class plus tests, and
/// the safety model applies uniformly.
/// </summary>
public interface ICleanupProvider
{
    /// <summary>A stable identifier, for settings and result correlation.</summary>
    string Id { get; }

    /// <summary>The named cause — "Gradle build cache", not a path.</summary>
    string Name { get; }

    SafetyTier Tier { get; }

    /// <summary>§7: what the user pays for this, stated up front.</summary>
    string WhatHappensOnNextUse { get; }

    /// <summary>Whether this toolchain is installed at all on this machine.</summary>
    Task<bool> IsPresentAsync(CancellationToken ct = default);

    /// <summary>
    /// Discard anything cached about the machine — resolved tool paths, the process snapshot,
    /// probed cache locations. Called once before a planning pass.
    ///
    /// This belongs to the provider because the provider owns those caches. An orchestrator
    /// holding its own collaborators and invalidating those instead would only appear to work.
    /// </summary>
    void InvalidateCaches();

    /// <summary>
    /// Exact paths and commands, with sizes measured. Never executed here.
    ///
    /// The §6.2 sketch also had an <c>EstimateBytesAsync</c>; it is deliberately absent. Producing
    /// an estimate means measuring, which means building the plan, so a separate method could only
    /// duplicate this work to return one number that <see cref="CleanupPlan.EstimatedBytes"/>
    /// already carries.
    /// </summary>
    Task<CleanupPlan> PlanAsync(CancellationToken ct = default);

    Task<CleanupResult> ExecuteAsync(CleanupPlan plan, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>§5.6 — assert the survivors.</summary>
    Task<VerificationResult> VerifyAsync(CleanupPlan plan, CancellationToken ct = default);
}
