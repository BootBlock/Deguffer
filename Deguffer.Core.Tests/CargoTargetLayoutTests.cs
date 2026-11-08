using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What inside a Cargo <c>target</c> the two Rust rows offer, and what they leave standing.
///
/// <para>The layouts are the ones Cargo 1.98 was measured writing: a profile holds <c>.cargo-lock</c>
/// and <c>.fingerprint</c>, a target triple's folder holds Cargo's own tag and its profiles, and a
/// project's build scripts keep their own output beside them. No Rust toolchain runs for any of it.</para>
/// </summary>
public sealed class CargoTargetLayoutTests : IDisposable
{
    private const string CargoTag =
        "Signature: 8a477f597d28d172789f06886806bc55\n# This file is a cache directory tag created by cargo.\n"
        + "# For information about cache directory tags see https://bford.info/cachedir/\n";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;

    public CargoTargetLayoutTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    // ---- §5.2: recognised children only --------------------------------------------------------

    /// <summary>
    /// The case that made <c>target</c> a tool root. A build script's release archives, screenshots and
    /// downloaded toolchain sit beside Cargo's profiles; only the profiles go, and everything else is
    /// named as a survivor, is still there afterwards, and §5.6 says so.
    /// </summary>
    [Fact]
    public async Task WhatABuildScriptKeepsInTargetSurvivesTheClean()
    {
        var target = CargoProject("app");
        var archives = Directory.CreateDirectory(Path.Combine(target, "dist-check")).FullName;
        File.WriteAllText(Path.Combine(archives, "app-1.0.0-x64.zip"), "release");
        var shots = Directory.CreateDirectory(Path.Combine(target, "shots")).FullName;
        File.WriteAllText(Path.Combine(shots, "window.png"), "png");
        var toolchain = Directory.CreateDirectory(Path.Combine(target, "toolchains", "zig-0.17.0")).FullName;
        File.WriteAllText(Path.Combine(toolchain, "zig.exe"), "zig");
        BuildDirectoryFixture.CreateCargoProfile(target, "release");

        var provider = BuildOutput();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            [Path.Combine(target, "debug"), Path.Combine(target, "release")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var kept in new[] { target, archives, shots, Path.Combine(target, "toolchains") })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path == kept && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(Path.Combine(target, "debug")));
        Assert.False(Directory.Exists(Path.Combine(target, "release")));
        Assert.True(File.Exists(Path.Combine(archives, "app-1.0.0-x64.zip")));
        Assert.True(File.Exists(Path.Combine(shots, "window.png")));
        Assert.True(File.Exists(Path.Combine(toolchain, "zig.exe")));
        Assert.True(File.Exists(Path.Combine(target, "CACHEDIR.TAG")));
    }

    /// <summary>
    /// A profile is recognised by what Cargo writes into it, not by its name: a custom profile's folder
    /// is offered under whatever it is called, and a folder called <c>release</c> without the lock file
    /// and fingerprints is somebody's own.
    /// </summary>
    [Fact]
    public async Task AProfileIsRecognisedByWhatCargoWritesIntoItNotByItsName()
    {
        var target = CargoProject("app");
        BuildDirectoryFixture.CreateCargoProfile(target, "profiling");
        var lookalike = Directory.CreateDirectory(Path.Combine(target, "release")).FullName;
        File.WriteAllText(Path.Combine(lookalike, ".cargo-lock"), string.Empty);
        var noLock = Directory.CreateDirectory(Path.Combine(target, "bench")).FullName;
        Directory.CreateDirectory(Path.Combine(noLock, ".fingerprint"));

        var plan = await BuildOutput().PlanAsync();

        Assert.Equal(
            [Path.Combine(target, "debug"), Path.Combine(target, "profiling")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == lookalike);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == noLock);
    }

    /// <summary>
    /// A target triple's folder is a target directory of its own, classified the same way: its profiles
    /// and its test scratch go, and what is not Cargo's inside it stays. Each step says which platform
    /// and profile it is, because a cross-compiled build is a choice the user makes separately.
    /// </summary>
    [Fact]
    public async Task ATargetTriplesFolderIsClassifiedLikeTheTargetItIsIn()
    {
        var target = CargoProject("app");
        var triple = Directory.CreateDirectory(Path.Combine(target, "x86_64-pc-windows-msvc")).FullName;
        File.WriteAllText(Path.Combine(triple, "CACHEDIR.TAG"), CargoTag);
        var profile = BuildDirectoryFixture.CreateCargoProfile(triple, "release");
        var scratch = Directory.CreateDirectory(Path.Combine(triple, "tmp")).FullName;
        var stray = Directory.CreateDirectory(Path.Combine(triple, "installer")).FullName;

        var plan = await BuildOutput().PlanAsync();

        Assert.Equal(
            [Path.Combine(target, "debug"), profile, scratch],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.Steps, s => s.Description.StartsWith("Rust build output (x86_64-pc-windows-msvc release) for app", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == stray);
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path == triple);
    }

    /// <summary>
    /// A folder inside <c>target</c> with a cache tag that is not Cargo's is not a target directory,
    /// however Cargo-like its contents look. A Zig cache keeps a <c>tmp</c> of its own, and reading the
    /// tag's header alone would offer it as Cargo's test scratch.
    /// </summary>
    [Fact]
    public async Task AFolderTaggedByAnotherToolIsNotLookedInside()
    {
        var target = CargoProject("app");
        var cache = Directory.CreateDirectory(Path.Combine(target, "zig-cache")).FullName;
        File.WriteAllText(Path.Combine(cache, "CACHEDIR.TAG"), "Signature: 8a477f597d28d172789f06886806bc55\n");
        Directory.CreateDirectory(Path.Combine(cache, "tmp"));
        BuildDirectoryFixture.CreateCargoProfile(cache, "debug");

        var plan = await BuildOutput().PlanAsync();

        Assert.Equal([Path.Combine(target, "debug")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == cache);
    }

    /// <summary>
    /// <c>doc</c> is offered where rustdoc's index and Cargo's lock are in it, and <c>package</c> never
    /// is: it holds the archive <c>cargo publish</c> uploads.
    /// </summary>
    [Fact]
    public async Task DocumentationIsOfferedByItsEvidenceAndAPackagedCrateIsNot()
    {
        var target = CargoProject("app");
        var doc = Directory.CreateDirectory(Path.Combine(target, "doc")).FullName;
        File.WriteAllText(Path.Combine(doc, ".lock"), string.Empty);
        File.WriteAllText(Path.Combine(doc, "crates.js"), "window.ALL_CRATES = [\"app\"];");
        var package = Directory.CreateDirectory(Path.Combine(target, "package")).FullName;
        File.WriteAllText(Path.Combine(package, "app-0.1.0.crate"), "crate");

        var plan = await BuildOutput().PlanAsync();

        Assert.Equal(
            [Path.Combine(target, "debug"), doc],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == package);
    }

    [Fact]
    public async Task AFolderNamedDocWithoutRustdocsIndexIsLeftAlone()
    {
        var target = CargoProject("app");
        var doc = Directory.CreateDirectory(Path.Combine(target, "doc")).FullName;
        File.WriteAllText(Path.Combine(doc, ".lock"), string.Empty);

        var plan = await BuildOutput().PlanAsync();

        Assert.Equal([Path.Combine(target, "debug")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == doc);
    }

    /// <summary>
    /// A link inside <c>target</c> is named and never looked through, even where it carries a
    /// profile's name and points at a real profile.
    /// </summary>
    [Fact]
    public async Task ALinkInsideTargetIsNamedAndNotFollowed()
    {
        var target = CargoProject("app");
        var elsewhere = BuildDirectoryFixture.CreateCargoProfile(_temp.CreateDirectory("elsewhere"), "release");
        var link = Path.Combine(target, "release");
        Junction.ToDirectory(link, elsewhere);

        var provider = BuildOutput();
        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(target, "debug")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == link && p.Reason.Contains("link", StringComparison.Ordinal));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(Path.Combine(elsewhere, "example.exe")));
    }

    /// <summary>
    /// Target directories nest only so deep: a third level is left alone and named rather than
    /// examined.
    /// </summary>
    [Fact]
    public async Task ANestedTargetDirectoryPastTheLimitIsLeftAlone()
    {
        var target = CargoProject("app");
        var directory = target;
        var names = new List<string>();

        for (var level = 0; level <= CargoTargetLayout.MaximumNesting; level++)
        {
            names.Add($"nested{level}");
            directory = Directory.CreateDirectory(Path.Combine(directory, $"nested{level}")).FullName;
            File.WriteAllText(Path.Combine(directory, "CACHEDIR.TAG"), CargoTag);
            BuildDirectoryFixture.CreateCargoProfile(directory, "debug");
        }

        var plan = await BuildOutput().PlanAsync();

        Assert.Equal(CargoTargetLayout.MaximumNesting + 1, plan.TargetedPaths.Count);
        Assert.DoesNotContain(Path.Combine(directory, "debug"), plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == directory);
    }

    /// <summary>
    /// A folder inside <c>target</c> that will not be listed is neither offered nor read as empty: the
    /// plan says it could not look, and what is readable beside it is still offered.
    /// </summary>
    [Fact]
    public async Task ANestedTargetDirectoryThatCannotBeListedIsSaidSo()
    {
        var target = CargoProject("app");
        var triple = Directory.CreateDirectory(Path.Combine(target, "aarch64-pc-windows-msvc")).FullName;
        File.WriteAllText(Path.Combine(triple, "CACHEDIR.TAG"), CargoTag);
        BuildDirectoryFixture.CreateCargoProfile(triple, "release");

        using var denied = new DeniedDirectory(triple);

        var plan = await BuildOutput().PlanAsync();

        Assert.Equal([Path.Combine(target, "debug")], plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Message.Contains(triple, StringComparison.Ordinal));
    }

    // ---- the incremental row -------------------------------------------------------------------

    /// <summary>
    /// One folder on one row. The incremental row offers each profile's <c>incremental</c> and nothing
    /// else; the build output row empties each profile around it, neither taking nor counting it.
    /// </summary>
    [Fact]
    public async Task EachProfilesIncrementalCacheIsOfferedOnItsOwnRowAndOnlyThere()
    {
        var target = CargoProject("app", writeIncremental: true);
        var debug = Path.Combine(target, "debug");
        var incremental = Path.Combine(debug, "incremental");

        var outputPlan = await BuildOutput().PlanAsync();
        var incrementalPlan = await Incremental().PlanAsync();

        var profileStep = Assert.IsType<ClearDirectoryStep>(Assert.Single(outputPlan.Steps));
        Assert.Equal(debug, profileStep.Path);
        Assert.Equal([incremental], profileStep.OwnedElsewhere);
        Assert.Equal(8192, outputPlan.EstimatedBytes);

        var cacheStep = Assert.IsType<DeleteDirectoryStep>(Assert.Single(incrementalPlan.Steps));
        Assert.Equal(incremental, cacheStep.Path);
        Assert.Equal(4096, incrementalPlan.EstimatedBytes);
        Assert.Equal(SafetyTier.RegenerableCache, incrementalPlan.Tier);

        Assert.DoesNotContain(outputPlan.ProtectedPaths, p => p.Path.StartsWith(incremental, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(incrementalPlan.ProtectedPaths, p => p.Path.StartsWith(debug, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The build output row alone leaves the incremental cache standing.</summary>
    [Fact]
    public async Task TheBuildOutputRowAloneLeavesTheIncrementalCache()
    {
        var target = CargoProject("app", writeIncremental: true);
        var debug = Path.Combine(target, "debug");

        var provider = BuildOutput();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(File.Exists(Path.Combine(debug, "example.exe")));
        Assert.False(Directory.Exists(Path.Combine(debug, "deps")));
        Assert.True(File.Exists(Path.Combine(debug, "incremental", "example-1abc", "query-cache.bin")));
    }

    /// <summary>The incremental row alone leaves every compiled dependency and binary standing.</summary>
    [Fact]
    public async Task TheIncrementalRowAloneLeavesTheBuild()
    {
        var target = CargoProject("app", writeIncremental: true);
        var debug = Path.Combine(target, "debug");

        var provider = Incremental();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(Path.Combine(debug, "incremental")));
        Assert.True(File.Exists(Path.Combine(debug, "example.exe")));
        Assert.True(File.Exists(Path.Combine(debug, "deps", "libdependency.rlib")));
    }

    /// <summary>
    /// Both rows in one run, in either order: neither one's §5.6 check reads the other's removal as a
    /// failure, and what neither offers is still there.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothRowsInOneRunEachVerify(bool incrementalFirst)
    {
        var target = CargoProject("app", writeIncremental: true);
        var archives = Directory.CreateDirectory(Path.Combine(target, "dist-check")).FullName;

        var output = BuildOutput();
        var incremental = Incremental();
        var outputPlan = await output.PlanAsync();
        var incrementalPlan = await incremental.PlanAsync();

        var reach = RunReach.Of([outputPlan, incrementalPlan]);
        var residue = new RunResidue();
        var order = incrementalFirst
            ? new (ICleanupProvider Provider, CleanupPlan Plan)[] { (incremental, incrementalPlan), (output, outputPlan) }
            : [(output, outputPlan), (incremental, incrementalPlan)];

        foreach (var (provider, plan) in order)
        {
            var result = await provider.ExecuteAsync(plan, reach, residue);
            Assert.True(result.Verification!.Passed, $"{provider.Name}: {result.Verification.Summary}");
        }

        Assert.False(Directory.Exists(Path.Combine(target, "debug", "incremental")));
        Assert.False(Directory.Exists(Path.Combine(target, "debug", "deps")));
        Assert.True(Directory.Exists(archives));
        Assert.True(File.Exists(Path.Combine(target, "CACHEDIR.TAG")));
    }

    /// <summary>
    /// An <c>incremental</c> that is a link is not offered: the row would remove what it points at. The
    /// profile it is in goes whole on the build output row, which takes the link and leaves what is
    /// behind it.
    /// </summary>
    [Fact]
    public async Task AnIncrementalCacheThatIsALinkIsNotOffered()
    {
        var target = CargoProject("app");
        var debug = Path.Combine(target, "debug");
        var elsewhere = _temp.CreateDirectory("moved-incremental");
        File.WriteAllText(Path.Combine(elsewhere, "query-cache.bin"), "state");
        Junction.ToDirectory(Path.Combine(debug, "incremental"), elsewhere);

        Assert.Empty((await Incremental().PlanAsync()).Steps);

        var provider = BuildOutput();
        var plan = await provider.PlanAsync();

        Assert.IsType<DeleteDirectoryStep>(Assert.Single(plan.Steps));
        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.False(Directory.Exists(debug));
        Assert.True(File.Exists(Path.Combine(elsewhere, "query-cache.bin")));
    }

    /// <summary>§7.4: the places named without planning cover every path the incremental row cleans.</summary>
    [Fact]
    public async Task CleanedPlacesCoverEveryPathTheIncrementalRowCleans()
    {
        CargoProject("app", writeIncremental: true);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(Incremental()));
        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(BuildOutput()));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private string CargoProject(string name, bool writeIncremental = false)
    {
        var root = _temp.CreateDirectory("src");
        _roots.Save([new SourceRoot(root)]);

        var target = BuildDirectoryFixture.CreateCargoProject(Path.Combine(root, name), writeIncremental: writeIncremental);
        File.WriteAllText(Path.Combine(target, "CACHEDIR.TAG"), CargoTag);
        return target;
    }

    private CargoTargetProvider BuildOutput()
    {
        var scanner = new FakeDirectoryScanner();

        return new CargoTargetProvider(
            _roots, new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()), FakeLiveTreeInspector.NothingLive,
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner);
    }

    private CargoIncrementalProvider Incremental()
    {
        var scanner = new FakeDirectoryScanner();

        return new CargoIncrementalProvider(
            _roots, new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()), FakeLiveTreeInspector.NothingLive,
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner);
    }
}
