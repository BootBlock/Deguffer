using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The incremental compilation caches inside a Rust project's <c>target</c> directory: one
/// <c>incremental</c> folder in each profile, where rustc keeps what it needs to re-compile only the
/// parts of the workspace's own crates that changed.
///
/// <para><b>Tier 1, where the rest of the profile is Tier 2.</b> rustc writes incremental state for the
/// workspace's own crates and never for a dependency from a registry, so removing it costs one full
/// compile of those crates and nothing else: the compiled dependencies beside it stay, and so does
/// every binary. Measured on one GPUI project's debug profile, it was 1.38 GB of 3.31 GB.</para>
///
/// <para><b>Its own row, and the build output row leaves it alone.</b> Both rows read one
/// <see cref="CargoTargetLayout"/>, and <see cref="CargoTargetProvider"/> empties each profile around
/// the <c>incremental</c> offered here, so one folder is offered on one row whichever is ticked.</para>
/// </summary>
public sealed class CargoIncrementalProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind CargoIncremental = CargoTargetLayout.Recognition with
    {
        Parts = (target, ct) => CargoTargetLayout.Of(target, ct).IncrementalCaches(),
    };

    public CargoIncrementalProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(CargoIncremental, roots, discovery, liveTrees, environment, runner, inspector, scanner, volumes)
    {
    }

    public override string Id => "cargo-incremental";

    public override string Name => "Rust incremental compilation caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next build of each project compiles its own crates in full once, and is incremental "
        + "again after that. Compiled dependencies and built programs are untouched, so nothing is "
        + "downloaded and no dependency is compiled again.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "Cargo, the build tool and package manager for Rust",
        Publisher = "the Rust project",
        Purpose = "When a Rust project is built, the compiler keeps an incremental folder in each "
            + "build profile, so that the next build re-compiles only what changed in the project's "
            + "own code. It grows with every build and is often the largest part of a debug build.",
        Recommendation = "Deguffer removes only these folders, from target directories with a "
            + "Cargo.toml beside them and Cargo's cache tag inside, and only inside the folders listed "
            + "under Settings, Source folders. The rest of each build stays, so the cost is one slower "
            + "build of your own code.",
    };

    // What a candidate is, and a candidate is a target directory: each step names its own part.
    protected override string Subject => "a Cargo target directory";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "Rust projects inside them, and nowhere else.";
}
