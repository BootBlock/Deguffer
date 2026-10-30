using System.Text.Json;
using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A Dart or Flutter package's <c>.dart_tool</c>: pub's resolution of the package's dependencies, and
/// what Flutter's build and the other Dart tools keep beside it.
///
/// <para><b>Recognised by what pub writes inside it, read, and by the package around it.</b>
/// <c>pub get</c> writes <c>.dart_tool\package_config.json</c> with <c>"generator": "pub"</c>, which
/// pub documents as "for <c>pub</c> this is always <c>'pub'</c>", and <c>"configVersion": 2</c>, the
/// only version it reads (<see href="https://github.com/dart-lang/pub/blob/master/lib/src/package_config.dart">package_config.dart</see>),
/// and writes <c>package_graph.json</c> beside it (<c>packageGraphPath</c> in
/// <see href="https://github.com/dart-lang/pub/blob/master/lib/src/entrypoint.dart">entrypoint.dart</see>).
/// Both go into the folder holding the package's <c>pubspec.lock</c>, beside its
/// <c>pubspec.yaml</c>, and all four are required. The generator alone is not enough: a test fixture
/// in a published package was measured carrying <c>"generator": "pub"</c> beside both pubspec files,
/// with no <c>package_graph.json</c>. A member of a pub workspace has neither the lock file nor the
/// configuration, which pub keeps only at the workspace's root, so a member's <c>.dart_tool</c> is
/// declined.</para>
///
/// <para><b>Git is asked for a second opinion, as it is for .NET's <c>obj</c>.</b> Two test
/// fixtures holding a <c>.dart_tool</c> are committed into the Flutter SDK's own repository, and were
/// measured there. pub's output is never committed (the configuration format's own text calls the
/// folder one that is "already in <c>.gitignore</c> files"), so a tracked file inside means the
/// folder is somebody's work.</para>
///
/// <para><b>§5.1 was considered and declined, for Cargo's reason.</b> <c>flutter clean</c> removes
/// the folder, with <c>build</c> and more, but per project, run in the project's own directory: one
/// subprocess for every package on the disk, from a toolchain an uninstalled SDK no longer has, to
/// remove the folder this already found.</para>
///
/// <para><b>Tier 2.</b> The next <c>pub get</c> resolves the dependencies again, from pub's cache
/// where that still holds them, and Flutter compiles what its build kept here again.</para>
/// </summary>
public sealed class DartToolProvider : BuildDirectoryProvider
{
    private const string PackageConfig = "package_config.json";

    /// <summary>
    /// Far beyond what pub writes: an entry per dependency, measured at 31 KB and 43 KB for two Flutter
    /// applications. See <see cref="BoundedFile"/> for why a file past it is not read.
    /// </summary>
    private const int PackageConfigMaximumBytes = 1024 * 1024;

    private static readonly BuildDirectoryKind DartTool = new()
    {
        DirectoryNames = [".dart_tool"],
        RequiredSiblings = ["pubspec.yaml", "pubspec.lock"],
        RequiredContents = ["package_graph.json"],
        ConfirmsContents = WrittenByPub,
        DeclinesTrackedDirectories = true,

        // The package's source, which nothing about the folder identifies but which a rule reaching
        // one level too far would take.
        ProtectedSiblings = ["lib"],
    };

    public DartToolProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(DartTool, roots, discovery, liveTrees, environment, runner, inspector, scanner, volumes)
    {
    }

    public override string Id => "dart-tool";

    public override string Name => "Dart and Flutter tool folders";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The package will not build or run until pub get resolves its dependencies again, which " +
        "Flutter and the Dart tools do by themselves on the next build, from pub's cache where it " +
        "still holds them and from the network where it does not. The pubspec.lock pins the versions, " +
        "so what comes back is what was there. Flutter's next build then compiles from the start.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "pub, the Dart and Flutter package manager, and the Flutter tool",
        Publisher = "Google",
        Purpose = "Each Dart or Flutter package keeps a .dart_tool folder holding pub's record of "
            + "where every dependency is, and what Flutter's build and the other Dart tools keep "
            + "between runs.",
        Recommendation = "Deguffer offers one only where pub's own package configuration is inside "
            + "it, the pubspec.yaml and pubspec.lock sit beside it, and git tracks nothing inside "
            + "it, and only inside the folders listed under Settings, Source folders.",
    };

    protected override string Subject => "a Dart tool folder";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "Dart and Flutter packages inside them, and nowhere else.";

    /// <summary>
    /// Whether <paramref name="directory"/>'s package configuration is one pub wrote: version 2, and
    /// pub named as its generator. A file that is missing, refused, too large or not JSON is not
    /// evidence of anything, and declines the folder.
    /// </summary>
    private static bool WrittenByPub(string directory)
    {
        using var document = BoundedJsonFile.Read(Path.Combine(directory, PackageConfig), PackageConfigMaximumBytes);

        return document is { RootElement: var record }
            && BoundedJsonFile.StringProperty(record, "generator") == "pub"
            && record.TryGetProperty("configVersion", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var number)
            && number == 2;
    }
}
