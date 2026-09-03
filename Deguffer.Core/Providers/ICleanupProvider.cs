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

    /// <summary>
    /// What this location is, for a reader who does not use the toolchain that wrote it. See
    /// <see cref="ProviderDescription"/> for why it is separate from
    /// <see cref="WhatHappensOnNextUse"/>.
    ///
    /// Required of every provider rather than defaulted, because a default would ship a row that
    /// names no publisher and explains nothing, and it would do so silently.
    /// </summary>
    ProviderDescription Description { get; }

    /// <summary>Whether this toolchain is installed at all on this machine.</summary>
    Task<bool> IsPresentAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether this provider looks only inside folders the user has approved, and has none.
    ///
    /// It is a fact about the configuration rather than about the machine, and it is the one the
    /// shell needs. "The tool is not installed" and "Deguffer has not been told where to look" are
    /// opposite in what they ask of the user: nothing can be done about the first, and adding a
    /// folder is the whole of the second. It is also the decision worth the most on the screen,
    /// because build output is usually the largest thing Deguffer can reclaim.
    ///
    /// Asked separately from <see cref="IsPresentAsync"/> rather than derived from it, because the
    /// two do not line up. A provider can be present and still have nowhere to look — the .NET
    /// build output is present whenever the SDK is, approved folders or not — and reading absence
    /// as the signal would leave that row claiming to be "already clear" about directories nobody
    /// ever enumerated.
    /// </summary>
    bool IsAwaitingSourceFolders { get; }

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

    /// <summary>
    /// The directories this provider owns whose unrecognised children are Tier 4, and the test that
    /// tells one from the other. Empty for a provider that owns no such directory.
    ///
    /// <para>§5.2 is enforced inside <see cref="PlanAsync"/> by a
    /// <see cref="DisposableChildSet"/>, which is enough while a plan is the only route to a
    /// deletion. §7.1 opens a second: Explore draws every directory on the drive and lets the user
    /// pick one out of the picture, and §5.2 is not scoped to a page — <c>gradle.properties</c>
    /// beside <c>.gradle\caches</c> is Tier 4 there exactly as it is here. So the rule is declared
    /// where something outside the provider can read it, rather than restated by Explore.</para>
    ///
    /// <para>Read outside a planning pass, so it must be cheap: a path this provider already knows,
    /// or resolves from an environment variable. A provider that would have to run a subprocess or
    /// walk the disk to answer declares nothing, which is correct — it has no root whose siblings
    /// need protecting, or it finds its roots rather than knowing them.</para>
    /// </summary>
    IReadOnlyList<ToolRoot> ToolRoots { get; }
}
