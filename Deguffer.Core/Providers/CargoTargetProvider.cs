using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A Rust project's <c>target</c> directory — everything <c>cargo build</c> produced, which is
/// routinely the largest single directory in a developer's source folders.
///
/// <para><b>Tier 2, not the Tier 1 the survey proposed, and the tier itself moves this time.</b>
/// Tier 1 means a slower next use. Restoring a <c>target</c> is not a slower build, it <em>is</em>
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
/// handful. §5.1's actual argument — that the tool reaches locations we do not know about — buys
/// almost nothing here: with no configuration, <c>cargo clean</c> removes the very directory
/// discovery already found. What it does reach that this does not is a <c>target</c> relocated by
/// <c>CARGO_TARGET_DIR</c> or <c>build.target-dir</c>, and that case is invisible to a path-based
/// provider rather than mishandled by it — the directory is simply not beside the manifest, so
/// nothing is found and nothing is claimed. And a machine whose Rust toolchain has been uninstalled
/// still has its <c>target</c> directories, which a command-based provider could not touch at all.</para>
///
/// <para>Recognition needs both halves. A <c>Cargo.toml</c> beside it says a Rust project is here,
/// and <c>CACHEDIR.TAG</c> inside says something marked this directory as a cache — which is the
/// part a folder somebody keeps by hand beside a manifest will not have. The file's contents are not
/// read, so the claim is "a tool following the cache-directory convention wrote this" rather than
/// "Cargo wrote this"; the conjunction with the manifest is what narrows it to Cargo. A <c>target</c>
/// predating the convention is declined, which costs disk space rather than data.</para>
/// </summary>
public sealed class CargoTargetProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind CargoTarget = new()
    {
        DirectoryNames = ["target"],
        RequiredSiblings = ["Cargo.toml"],
        RequiredContents = ["CACHEDIR.TAG"],

        // Neither identifies the directory, and both are what a rule reaching one level too far
        // would take with it: the crate's own source, and the lock file pinning what it compiled
        // against. §5.6 asks what must survive, not what recognition read.
        ProtectedSiblings = ["src", "Cargo.lock"],
    };

    public CargoTargetProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(CargoTarget, roots, discovery, liveTrees, environment, runner, inspector, scanner)
    {
    }

    public override string Id => "cargo-target";

    public override string Name => "Rust build output";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next build recompiles the project and every dependency it uses, which for a large " +
        "workspace is minutes to hours rather than seconds. Sources come from the Cargo registry " +
        "cache where that is still present, and from the network where it is not. Anything you built " +
        "and are running from target goes with it.";

    /// <summary>
    /// Whose files these are and what they are for. See <see cref="ProviderDescription"/>.
    /// </summary>
    public override ProviderDescription Description { get; } = new()
    {
        Application = "Cargo, the build tool and package manager for Rust",
        Publisher = "the Rust project",
        Purpose = "Each Rust project keeps everything cargo build produced in a target directory "
            + "beside its manifest: compiled dependencies, intermediate artefacts and the binaries "
            + "themselves, separately for every profile and feature set it has been built with.",
        Recommendation = "Only when you need the space. Restoring one is not a slower build, it "
            + "is the build — every dependency is compiled from source again, which is where the "
            + "gigabytes came from. Deguffer offers a target directory only where a Cargo.toml "
            + "sits beside it and a tool has marked it as a cache.",
    };

    protected override string Subject => "Rust build output";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "Rust projects inside them, and nowhere else.";
}
