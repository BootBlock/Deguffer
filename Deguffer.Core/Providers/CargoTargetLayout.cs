using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Cargo is recognised to have written inside one <c>target</c> directory, read once and divided
/// between the two rows that offer it: the build output (<see cref="CargoTargetProvider"/>) and the
/// incremental compilation caches inside it (<see cref="CargoIncrementalProvider"/>).
///
/// <para><b>Why <c>target</c> is a tool root rather than a disposable directory.</b> Cargo's own tag
/// says the whole directory is a cache, and <c>cargo clean</c> removes all of it. But a project's own
/// build scripts write there too, because it is the one folder every Rust repository ignores. Measured
/// on one machine: a project's release archives and screenshots in <c>target\dist-check</c> and
/// <c>target\shots</c>, and a downloaded Zig toolchain in another project's <c>target\toolchains</c>.
/// None of that is Cargo's to re-create, so §5.2 applies: recognised children only, and everything
/// else is left standing and named.</para>
///
/// <para><b>What is recognised, and by what.</b></para>
/// <list type="bullet">
/// <item>A <b>profile directory</b> (<c>debug</c>, <c>release</c>, or a custom profile's own name), by
/// what Cargo writes into every one: its <c>.cargo-lock</c> file and its <c>.fingerprint</c> directory.
/// Read by content rather than by name, because a custom profile can be called anything.</item>
/// <item>A <b>nested target directory</b>: a target triple's (<c>target\x86_64-pc-windows-msvc</c>), or
/// a whole target directory another tool pointed Cargo at, such as rust-analyzer's. Recognised by the
/// cache directory tag carrying Cargo's own line, because Cargo writes one into every target directory
/// it creates, and classified the same way, down to <see cref="MaximumNesting"/>.</item>
/// <item><b><c>doc</c></b>, by the <c>.lock</c> Cargo takes on it and the <c>crates.js</c> index
/// rustdoc writes.</item>
/// <item><b><c>tmp</c></b>, by name alone, because it is the one directory Cargo documents and creates
/// without writing anything into it: the scratch folder it hands integration tests as
/// <c>CARGO_TARGET_TMPDIR</c>.</item>
/// </list>
///
/// <para><b><c>package</c> is not offered</b>, although Cargo writes it. It holds the <c>.crate</c>
/// archive <c>cargo package</c> made, which is what <c>cargo publish</c> uploads, so it is a release
/// artefact in the sense <c>dist-check</c> is, and it is small. Files directly inside a target directory
/// are never offered either: they are Cargo's records of the toolchain, a few kilobytes, and the tag
/// that recognised the directory.</para>
///
/// <para><b>The incremental caches are their own row.</b> A profile's <c>incremental</c> holds rustc's
/// state for re-compiling the workspace's own crates, and removing it costs one full compile of those
/// crates and none of the dependencies. That is Tier 1, where the rest of the profile is Tier 2, so
/// each profile is offered on the build output row emptied around its <c>incremental</c>, and the
/// <c>incremental</c> on its own row.</para>
/// </summary>
public sealed class CargoTargetLayout
{
    /// <summary>
    /// How many target directories deep the classification goes below the recognised one. Two covers
    /// the deepest layout measured or documented: rust-analyzer's own target directory inside the
    /// project's, holding a target triple's. A nested one below that is left alone, which costs disk
    /// space rather than data.
    /// </summary>
    public const int MaximumNesting = 2;

    /// <summary>
    /// What makes a <c>target</c> Cargo's, for both rows. Recognition needs both halves: a
    /// <c>Cargo.toml</c> beside it says a Rust project is here, and a cache directory tag inside it says
    /// something marked the folder as a cache, which a folder somebody keeps by hand beside a manifest
    /// will not have. Only the tag's header is required here, unlike <see cref="CargoTag"/>, because
    /// the text after it is unspecified and was not verified for every Cargo release that wrote one,
    /// and the manifest beside it already says which tool this is. Cargo writes the tag only into a
    /// directory it creates
    /// (<see href="https://github.com/rust-lang/cargo/blob/master/crates/cargo-util/src/paths.rs">paths.rs</see>).
    /// </summary>
    public static BuildDirectoryKind Recognition { get; } = new()
    {
        DirectoryNames = ["target"],
        RequiredSiblings = ["Cargo.toml"],
        RequiredMarkers = [MarkerFile.AnyCacheDirectoryTag],

        // Neither identifies the directory, and both are what a rule reaching one level too far
        // would take with it: the crate's own source, and the lock file pinning what it compiled
        // against. §5.6 asks what must survive, not what recognition read.
        ProtectedSiblings = ["src", "Cargo.lock"],
    };

    /// <summary>
    /// Cargo's own line in the tags it writes, after the header. Required of a nested target
    /// directory, which has no manifest beside it to say whose it is: a folder a build script made
    /// inside <c>target</c> can carry a tag of its own and a <c>tmp</c> of its own.
    /// </summary>
    public static readonly MarkerFile CargoTag =
        MarkerFile.CacheDirectoryTag("# This file is a cache directory tag created by cargo.");

    private const string Incremental = "incremental";

    private const string LinkReason =
        "A link rather than a folder, so what it points at was never examined.";

    private const string UnrecognisedReason =
        "Not something Cargo is recognised to have written, so it is left alone. A project's build "
        + "scripts often keep their own output in the target folder.";

    private const string TooDeepReason =
        "A target folder nested deeper than Deguffer looks, so nothing inside it was examined.";

    private readonly List<Profile> _profiles = [];
    private readonly List<BuildDirectoryPart> _otherOutput = [];
    private readonly List<(string Path, string Reason)> _leftAlone = [];
    private readonly List<string> _unreadable = [];

    private CargoTargetLayout()
    {
    }

    /// <summary>One profile directory, and its incremental cache where it holds one.</summary>
    private sealed record Profile(string Path, string Label, string? IncrementalCache);

    /// <summary>Reads what Cargo wrote inside <paramref name="target"/>, a recognised target directory.</summary>
    public static CargoTargetLayout Of(string target, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var layout = new CargoTargetLayout();
        layout.Classify(target, labelPrefix: null, nesting: 0, ct);
        return layout;
    }

    /// <summary>The build output row's parts: every profile, emptied around its incremental cache, and <c>doc</c> and <c>tmp</c>.</summary>
    public BuildDirectoryParts BuildOutput() => new(
        [
            .. _profiles.Select(profile => new BuildDirectoryPart(
                profile.Path,
                $"Rust build output ({profile.Label})",
                profile.IncrementalCache is { } cache ? [cache] : [])),
            .. _otherOutput,
        ],
        _leftAlone,
        _unreadable);

    /// <summary>The incremental cache row's parts: each profile's <c>incremental</c>.</summary>
    public BuildDirectoryParts IncrementalCaches() => new(
        [
            .. _profiles
                .Where(profile => profile.IncrementalCache is not null)
                .Select(profile => new BuildDirectoryPart(
                    profile.IncrementalCache!,
                    $"Rust incremental compilation cache ({profile.Label})",
                    [])),
        ],
        _leftAlone,
        _unreadable);

    private void Classify(string directory, string? labelPrefix, int nesting, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var scan = ChildDirectories.Under(directory);

        if (scan.Unreadable)
        {
            _unreadable.Add(directory);
            return;
        }

        // The listing hands back extended-length paths, and a plan never holds one.
        _leftAlone.AddRange(scan.Links.Select(link => (LongPath.Display(link.FullName), LinkReason)));

        foreach (var child in scan.Directories)
        {
            var name = child.Name;
            var path = LongPath.Display(child.FullName);
            var label = labelPrefix is null ? name : $"{labelPrefix} {name}";

            if (IsProfile(path))
            {
                _profiles.Add(new Profile(path, label, IncrementalCacheIn(path)));
            }
            else if (name.Equals("tmp", StringComparison.OrdinalIgnoreCase))
            {
                _otherOutput.Add(new BuildDirectoryPart(path, $"Rust integration test scratch ({label})", []));
            }
            else if (name.Equals("doc", StringComparison.OrdinalIgnoreCase) && IsDocumentation(path))
            {
                _otherOutput.Add(new BuildDirectoryPart(path, $"Rust documentation ({label})", []));
            }
            else if (CargoTag.IsIn(path))
            {
                if (nesting < MaximumNesting)
                {
                    Classify(path, label, nesting + 1, ct);
                }
                else
                {
                    _leftAlone.Add((path, TooDeepReason));
                }
            }
            else
            {
                _leftAlone.Add((path, UnrecognisedReason));
            }
        }
    }

    // Probes that decide what a folder is, so a refusal is not evidence for the classification and
    // reads as "not this": the folder is then left alone and named, which costs disk space.
    private static bool IsProfile(string path) =>
        IsFile(Path.Combine(path, ".cargo-lock")) && IsDirectory(Path.Combine(path, ".fingerprint"));

    private static bool IsDocumentation(string path) =>
        IsFile(Path.Combine(path, ".lock")) && IsFile(Path.Combine(path, "crates.js"));

    /// <summary>
    /// The profile's incremental cache, or null where it has none. A link of that name is not one: the
    /// row offering it would remove what it points at, so it is left to the profile's own removal, which
    /// takes the link and never what is behind it.
    /// </summary>
    private static string? IncrementalCacheIn(string profile)
    {
        var path = Path.Combine(profile, Incremental);

        return LongPath.ProbeDirectory(path, out var isLink) is PathPresence.Present && isLink is false ? path : null;
    }

    private static bool IsFile(string path) => LongPath.ProbeFile(path, out var isLink) is PathPresence.Present && isLink is false;

    private static bool IsDirectory(string path) => LongPath.ProbeDirectory(path, out var isLink) is PathPresence.Present && isLink is false;
}
