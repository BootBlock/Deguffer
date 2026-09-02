using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A Dart or Flutter project's <c>build</c> directory — compiled output and intermediate artefacts.
///
/// <para><c>build</c> is the weakest directory name in this whole category. It is an ordinary word,
/// it is what half the build systems in existence call their output, and plenty of people keep one
/// by hand. So the evidence has to come entirely from what stands beside it, and both markers are
/// required rather than either: <c>pubspec.yaml</c> says a Dart package is here, and
/// <c>.dart_tool</c> says the toolchain has actually run in it. A <c>build</c> beside a
/// <c>pubspec.yaml</c> in a package nobody has ever built is declined, which costs nothing, because
/// there is nothing in it.</para>
///
/// <para>Tier 2 for the time rather than the bandwidth: rebuilding a Flutter application is minutes,
/// and the platform-specific artefacts underneath are regenerated from source and from the pub cache
/// — which <c>docs/cache-locations.md</c> already covers as a separate subject.</para>
/// </summary>
public sealed class DartBuildProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind DartBuild = new()
    {
        DirectoryNames = ["build"],
        RequiredSiblings = ["pubspec.yaml", ".dart_tool"],
    };

    public DartBuildProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(DartBuild, roots, discovery, liveTrees, environment, runner, inspector, scanner)
    {
    }

    public override string Id => "dart-build";

    public override string Name => "Dart and Flutter build output";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next build recompiles the project from scratch, which on a Flutter application is " +
        "minutes rather than seconds. Nothing is lost: it is all produced from your source and the " +
        "pub cache.";

    protected override string Subject => "Dart or Flutter build output";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "Dart and Flutter projects inside them, and nowhere else.";
}
