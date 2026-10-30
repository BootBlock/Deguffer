using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A Dart or Flutter package's <c>.dart_tool</c>. The measured hazards are the cases: test fixtures
/// carrying a package configuration, committed into the Flutter SDK's own repository and shipped in
/// published packages, and pub workspace members, whose configuration lives at the workspace root.
/// </summary>
public sealed class DartToolProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;

    public DartToolProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private string Git => _environment.FindExecutable("git")!;

    [Fact]
    public async Task PlansAPackagesToolFolderWithItsMeasuredSizeAtTierTwo()
    {
        var tool = BuildDirectoryFixture.CreateDartPackage(Path.Combine(ApproveRoot(), "app"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([tool], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
    }

    /// <summary>
    /// Each piece of the evidence, missing or wrong on its own. <c>generator</c> and
    /// <c>configVersion</c> missing are a configuration a person or another tool wrote.
    /// <c>packageGraph</c> is the measured fixture shape: <c>"generator": "pub"</c> and both pubspec
    /// files, with no <c>package_graph.json</c>. <c>lockFile</c> and <c>packageConfig</c> are each half
    /// of a pub workspace member, which has neither. <c>pubspec</c> is a folder with no package around
    /// it, and the last two are another generator and a format pub does not write.
    /// </summary>
    [Theory]
    [InlineData("generator")]
    [InlineData("configVersion")]
    [InlineData("packageGraph")]
    [InlineData("lockFile")]
    [InlineData("pubspec")]
    [InlineData("packageConfig")]
    [InlineData("otherGenerator")]
    [InlineData("laterVersion")]
    public async Task AToolFolderMissingAnyPieceOfTheEvidenceIsNotRecognised(string missing)
    {
        var project = Path.Combine(ApproveRoot(), "app");
        var tool = missing switch
        {
            "generator" => BuildDirectoryFixture.CreateDartPackage(project, generator: null),
            "configVersion" => BuildDirectoryFixture.CreateDartPackage(project, configVersion: null),
            "packageGraph" => BuildDirectoryFixture.CreateDartPackage(project, writePackageGraph: false),
            "lockFile" => BuildDirectoryFixture.CreateDartPackage(project, writeLockFile: false),
            "pubspec" => BuildDirectoryFixture.CreateDartPackage(project, writePubspec: false),
            "packageConfig" => BuildDirectoryFixture.CreateDartPackage(project, writePackageConfig: false),
            "otherGenerator" => BuildDirectoryFixture.CreateDartPackage(project, generator: "build_runner"),
            "laterVersion" => BuildDirectoryFixture.CreateDartPackage(project, configVersion: 3),
            _ => throw new ArgumentOutOfRangeException(nameof(missing), missing, null),
        };

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == tool);
        Assert.True(LongPath.DirectoryExists(tool));
    }

    /// <summary>A package configuration that is not JSON is not evidence of anything.</summary>
    [Fact]
    public async Task APackageConfigurationThatIsNotJsonIsNotRecognised()
    {
        var tool = BuildDirectoryFixture.CreateDartPackage(Path.Combine(ApproveRoot(), "app"));
        File.WriteAllText(Path.Combine(tool, "package_config.json"), "generator: pub");

        Assert.Empty((await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// The Flutter SDK case. A <c>.dart_tool</c> git tracks a file in is somebody's committed work,
    /// whatever is inside it, so it is held back and the plan says why, in its own sentence rather
    /// than among the folders recognition could not confirm.
    /// </summary>
    [Fact]
    public async Task LeavesAToolFolderGitTracksAFileInAlone()
    {
        var repository = Path.Combine(ApproveRoot(), "sdk");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var committed = BuildDirectoryFixture.CreateDartPackage(Path.Combine(repository, "fixtures", "committed"));
        var ignored = BuildDirectoryFixture.CreateDartPackage(Path.Combine(repository, "tools", "ignored"));

        _environment.WithExecutable("git");
        var runner = new FakeProcessRunner().Responding(Git, "ls-files", "fixtures/committed/.dart_tool/package_config.json\0");

        var provider = CreateProvider(runner);
        var plan = await provider.PlanAsync();

        Assert.Equal([ignored], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(committed, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("tracked in git", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("could not be confirmed", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.DirectoryExists(ignored));
        Assert.True(LongPath.FileExists(Path.Combine(committed, "package_config.json")));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>Git present and unable to answer declines, as it does for <c>obj</c>.</summary>
    [Fact]
    public async Task LeavesAToolFolderAloneWhenGitCannotAnswer()
    {
        var repository = Path.Combine(ApproveRoot(), "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var tool = BuildDirectoryFixture.CreateDartPackage(Path.Combine(repository, "app"));

        _environment.WithExecutable("git");
        var runner = new FakeProcessRunner().Responding(Git, "ls-files", string.Empty, exitCode: 128);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(tool, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not be checked against git", StringComparison.Ordinal));
    }

    /// <summary>
    /// The kinds that do not ask git are not changed by it: a Cargo <c>target</c> in the same
    /// repository is offered without a git process being started.
    /// </summary>
    [Fact]
    public async Task OnlyTheKindsThatAskGitStartIt()
    {
        var repository = Path.Combine(ApproveRoot(), "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var target = BuildDirectoryFixture.CreateCargoProject(Path.Combine(repository, "crate"));

        _environment.WithExecutable("git");
        var runner = new FakeProcessRunner().Responding(Git, "ls-files", "crate/target/committed\0");
        var scanner = new FakeDirectoryScanner();

        var plan = await new CargoTargetProvider(
            _roots, new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()), FakeLiveTreeInspector.NothingLive,
            _environment, runner, FakeProcessInspector.NothingRunning, scanner).PlanAsync();

        Assert.Equal([target], plan.TargetedPaths);
        Assert.Empty(runner.Invocations);
    }

    /// <summary>§5.6: the package, its pubspec files and its source survive, on disk.</summary>
    [Fact]
    public async Task ThePackageAndItsSourceSurviveTheRemoval()
    {
        var project = Path.Combine(ApproveRoot(), "app");
        var tool = BuildDirectoryFixture.CreateDartPackage(project);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p => p.Path == project && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(project, "pubspec.yaml") && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(project, "pubspec.lock") && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(project, "lib") && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.DirectoryExists(tool));
        Assert.True(LongPath.FileExists(Path.Combine(project, "pubspec.yaml")));
        Assert.True(LongPath.FileExists(Path.Combine(project, "pubspec.lock")));
        Assert.True(LongPath.FileExists(Path.Combine(project, "lib", "main.dart")));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryFolderThePlanRemoves()
    {
        BuildDirectoryFixture.CreateDartPackage(Path.Combine(ApproveRoot(), "team", "app"));

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    [Fact]
    public void IsShipped()
    {
        using var shipped = new ShippedProviders();

        Assert.Single(shipped.Providers, p => p is DartToolProvider);
    }

    private string ApproveRoot()
    {
        var root = Path.Combine(_temp.Path, "src");
        Directory.CreateDirectory(root);
        _roots.Save([new SourceRoot(root)]);
        return root;
    }

    private DartToolProvider CreateProvider(FakeProcessRunner? runner = null)
    {
        var scanner = new FakeDirectoryScanner();

        return new DartToolProvider(
            _roots,
            new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()),
            FakeLiveTreeInspector.NothingLive,
            _environment,
            runner ?? new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner);
    }
}
