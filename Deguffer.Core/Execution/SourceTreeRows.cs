using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// The rows <see cref="CleanupPlanner.CreateDefault"/> builds that search the user's approved source
/// folders, built together because they share one discovery and the surveys two or three of them divide.
/// </summary>
internal static class SourceTreeRows
{
    /// <summary>
    /// Every row that searches the user's approved source folders, sharing <paramref name="sourceTrees"/>.
    /// Building them registers each one's names with it, which is how the benchmark walks with the set
    /// a scan walks with rather than a copy of it.
    /// </summary>
    public static IReadOnlyList<ICleanupProvider> Create(
        SourceRootStore roots,
        SourceDirectoryDiscovery sourceTrees,
        ILiveTreeInspector liveTrees,
        IUserEnvironment environment,
        IDirectoryScanner scanner)
    {
        // One reading of each Cargo target directory for both Rust rows, which divide it between them:
        // the build output row leaves each profile's incremental cache to the other, and that holds
        // only while both read the same layout.
        var cargoTargets = new CargoTargetSurvey();

        // One reading of each Godot project's settings and data folder for the three Godot rows, the
        // two Godot 4 ones dividing its data folder between them as the Rust rows divide a target.
        var godotProjects = new GodotProjectSurvey();

        return
        [
            new DotNetObjProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new VisualStudioSolutionCacheProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new UnityLibraryProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new UnrealIntermediateProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new UnrealProjectDerivedDataProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new CargoTargetProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner, targets: cargoTargets),
            new CargoIncrementalProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner, targets: cargoTargets),
            new GodotImportCacheProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner, projects: godotProjects),
            new GodotShaderCacheProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner, projects: godotProjects),
            new Godot3ImportCacheProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner, projects: godotProjects),
            new NodeModulesProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new PythonVirtualEnvironmentProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new PytestCacheProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new MypyCacheProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new RuffCacheProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new ToxEnvironmentProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new NoxEnvironmentProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new DartToolProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new NxWorkspaceCacheProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new GitLfsObjectProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
        ];
    }
}
