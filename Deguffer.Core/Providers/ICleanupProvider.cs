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

    /// <summary>Reclaimable bytes, measured but not acted on.</summary>
    Task<long> EstimateBytesAsync(CancellationToken ct = default);

    /// <summary>Exact paths and commands. Never executed here.</summary>
    Task<CleanupPlan> PlanAsync(CancellationToken ct = default);

    Task<CleanupResult> ExecuteAsync(CleanupPlan plan, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>§5.6 — assert the survivors.</summary>
    Task<VerificationResult> VerifyAsync(CleanupPlan plan, CancellationToken ct = default);
}
