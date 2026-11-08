using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <c>cargo build</c> produced inside a Rust project's <c>target</c> directory, which is routinely
/// the largest single directory in a developer's source folders.
///
/// <para><b>Recognised children only, never the whole directory.</b> A project's own build scripts
/// keep their output in <c>target</c> too, so it is treated as a tool root (§5.2), and
/// <see cref="CargoTargetLayout"/> says what inside it is Cargo's. Each profile is offered emptied around
/// its incremental cache, which <see cref="CargoIncrementalProvider"/> offers at Tier 1.</para>
///
/// <para><b>Tier 2, not the Tier 1 the survey proposed, and the tier itself moves this time.</b>
/// Tier 1 means a slower next use. Restoring a profile is not a slower build, it <em>is</em>
/// the build: every dependency is compiled from source, per profile and per feature set, which is
/// where the five to twenty gigabytes came from in the first place. That is the same argument the
/// previous phase used to move vcpkg — a binary cache whose entries are recovered by compiling
/// rather than by downloading — and it applies here with more force, because there is no cache to
/// fall back on at all. It also needs the network unless the registry cache is intact.</para>
///
/// <para><b>§5.1 was considered and declined, and the reason is worth stating because this is the
/// only one of these four with a command.</b> <c>cargo clean</c> exists, but it is a per-project
/// command run in the project's own directory, so it would mean one subprocess for every Rust
/// project on the disk, each able to hang, against a per-step selection model where the user picks a
/// handful. It also removes the whole of <c>target</c>, which is exactly what §5.2 rules out here:
/// <c>cargo clean</c> takes a project's release archives with it. What it does reach that this does
/// not is a <c>target</c> relocated by <c>CARGO_TARGET_DIR</c> or <c>build.target-dir</c>, and that
/// case is invisible to a path-based provider rather than mishandled by it — the directory is simply
/// not beside the manifest, so nothing is found and nothing is claimed. And a machine whose Rust
/// toolchain has been uninstalled still has its <c>target</c> directories, which a command-based
/// provider could not touch at all.</para>
/// </summary>
public sealed class CargoTargetProvider : BuildDirectoryProvider
{
    public CargoTargetProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null,
        CargoTargetSurvey? targets = null)
        : base(KindOver(targets ??= new CargoTargetSurvey()), roots, discovery, liveTrees, environment, runner, inspector, scanner, volumes)
    {
        _targets = targets;
    }

    private readonly CargoTargetSurvey _targets;

    public override void InvalidateCaches()
    {
        base.InvalidateCaches();
        _targets.Invalidate();
    }

    /// <summary>
    /// Recognition shared with the other Rust row, and this row's half of the reading both share. An
    /// unshared survey is correct and reads each directory for this row alone, which is what a test
    /// wants; the planner hands both rows one.
    /// </summary>
    private static BuildDirectoryKind KindOver(CargoTargetSurvey targets) => CargoTargetLayout.Recognition with
    {
        Parts = (target, ct) => targets.Of(target, ct).BuildOutput(),
    };

    public override string Id => "cargo-target";

    public override string Name => "Rust build output";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next build recompiles the project and every dependency it uses, which for a large " +
        "workspace is minutes to hours rather than seconds. Sources come from the Cargo registry " +
        "cache where that is still present, and from the network where it is not. Anything you built " +
        "and are running from target goes with it. Anything else your own scripts keep in target stays.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "Cargo, the build tool and package manager for Rust",
        Publisher = "the Rust project",
        Purpose = "Each Rust project keeps everything cargo build produced in a target directory "
            + "beside its manifest: compiled dependencies, intermediate artefacts and the binaries "
            + "themselves, separately for every profile, target platform and feature set it has been "
            + "built with.",
        Recommendation = "Restoring a build is not a slower build, it is the build — every dependency "
            + "is compiled from source again, which is where the gigabytes came from. Deguffer "
            + "looks only in a target directory with a Cargo.toml beside it and Cargo's cache tag "
            + "inside, and removes only the profile folders, documentation and test scratch Cargo "
            + "wrote there. Anything else in it, such as release archives a build script made, stays.",
    };

    protected override string Subject => "Rust build output";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "Rust projects inside them, and nowhere else.";
}
