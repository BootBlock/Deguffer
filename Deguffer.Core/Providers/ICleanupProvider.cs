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

    /// <summary>
    /// Whether this provider's steps are parts of one location or items the user chooses between. See
    /// <see cref="StepGrain"/> for why it is declared rather than read off the number of steps.
    ///
    /// Required of every provider rather than defaulted, for the reason <see cref="Description"/> is:
    /// a default would decide silently, for every provider nobody thought about, how its row is chosen.
    /// </summary>
    StepGrain Grain { get; }

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
    /// <param name="keep">
    /// The user's guard on recently touched files, if they set one. It reaches planning rather than
    /// only execution because the estimate and the deletion have to describe the same set of files:
    /// a preview naming bytes the clean will not take is §5.4's broken promise arriving from the
    /// other direction. <see cref="MinimumAge.Off"/> by default, which is the shipped preference.
    /// </param>
    Task<CleanupPlan> PlanAsync(MinimumAge keep = default, CancellationToken ct = default);

    /// <param name="runReach">
    /// What the whole run may destroy, for §5.6's negative. It sits beside the plan rather than
    /// inside it because it belongs to the run: a verifier that saw only this provider's targets
    /// would report a folder another provider deleted as one something outside Deguffer removed.
    /// Null means this plan is the whole run, which is what a provider executed on its own is.
    /// </param>
    /// <param name="residue">
    /// What the run's removals have left standing so far, which this execution adds to and verifies
    /// against (§5.6). One record for the whole run, for the reason <paramref name="runReach"/> is one:
    /// a folder another provider's removal went into is still a folder this run went into. Null means
    /// this plan is the whole run.
    /// </param>
    Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        RunReach? runReach = null,
        RunResidue? residue = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>§5.6 — assert the survivors.</summary>
    Task<VerificationResult> VerifyAsync(
        CleanupPlan plan,
        RunReach? runReach = null,
        CancellationToken ct = default);

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
    /// or resolves from an environment variable. A provider that would have to run a subprocess,
    /// walk the disk or read the process table answers in
    /// <see cref="DiscoverToolRootsAsync"/> instead.</para>
    /// </summary>
    IReadOnlyList<ToolRoot> ToolRoots { get; }

    /// <summary>
    /// The same declaration for what this provider can only protect once it has asked the machine.
    /// Empty for a provider whose <see cref="ToolRoots"/> are the whole of what it owns.
    ///
    /// <para><b>It exists because §7.1's refusal set was smaller than §5.2's.</b> A tool's location
    /// moves — <c>go env</c>, <c>pio system info</c>, <c>pnpm store path</c>, <c>conda info</c> and
    /// Maven's <c>settings.xml</c> each report one that is not the documented default — and
    /// <see cref="ToolRoots"/> cannot say so without paying a subprocess on every path Explore is
    /// asked about. Declaring only the default left Explore allowing the moved directory and
    /// everything a plan protects inside it, while the Storage page refused both.</para>
    ///
    /// <para><b>A path something is using right now is declared here too</b>, as a root that
    /// recognises no child, because that is what "nothing in here may go" is in this vocabulary. A
    /// plan holds those back and asserts them under §5.6, which makes them paths a provider names
    /// as protected, and §7.1 refuses every such path. They are the one kind of declaration that
    /// ages: what is running changes, so the answer is read again whenever Explore rebuilds its
    /// policy rather than kept for the life of the process.</para>
    ///
    /// <para>Asked once, off the UI thread, before Explore will act — never per path. The probes
    /// behind it are the ones a planning pass already runs, and each provider caches its answer
    /// until <see cref="InvalidateCaches"/>, so a pass that follows pays for none of them twice.</para>
    /// </summary>
    Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default);
}
