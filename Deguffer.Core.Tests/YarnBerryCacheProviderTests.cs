using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The global cache of Yarn 2 and later. No Yarn was installed on the machine these were written on,
/// so the replies are Berry's as its source prints them and its release bundles printed them in a
/// scratch folder: <c>-v</c> the bare version, and <c>config get --json</c> one JSON value.
/// </summary>
public sealed class YarnBerryCacheProviderTests : IDisposable
{
    private const string Archive = "lodash-npm-4.17.21-6382451519-10c0.zip";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public YarnBerryCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private string Yarn => _environment.FindExecutable("yarn")!;

    private string YarnFolder => Path.Combine(_environment.LocalAppData, "Yarn");

    private string GlobalFolder => Path.Combine(YarnFolder, "Berry");

    private string Cache => Path.Combine(GlobalFolder, "cache");

    private YarnBerryCacheProvider CreateProvider(FakeProcessRunner? runner = null) =>
        new(_environment, runner ?? new FakeProcessRunner(), FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>
    /// A yarn on the PATH that says it is <paramref name="version"/> and, as Berry, reports
    /// <paramref name="globalFolder"/> and <paramref name="cleanEnabled"/>. Berry before 4.6 fails the
    /// second question, which a null stands for. Any other question fails the test.
    /// </summary>
    private FakeProcessRunner YarnAnswering(string version = "4.18.1", string? globalFolder = null, string? cleanEnabled = "true")
    {
        _environment.WithExecutable("yarn");
        globalFolder ??= GlobalFolder;

        return new FakeProcessRunner().Replying(Yarn, arguments => arguments switch
        {
            "-v" => new CommandOutcome(0, version + "\n", string.Empty),
            "config get globalFolder --json" => new CommandOutcome(0, System.Text.Json.JsonSerializer.Serialize(globalFolder) + "\n", string.Empty),
            "config get enableCacheClean --json" => cleanEnabled is null
                ? new CommandOutcome(1, "Usage Error: Couldn't find a configuration settings named \"enableCacheClean\"\n", string.Empty)
                : new CommandOutcome(0, cleanEnabled + "\n", string.Empty),
            _ => throw new Xunit.Sdk.XunitException($"Yarn was asked '{arguments}'."),
        });
    }

    private string PopulateCache(string? cache = null)
    {
        cache ??= Cache;
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, Archive), new byte[4096]);
        File.WriteAllBytes(Path.Combine(cache, "@types-node-npm-20.11.5-0f3a9c2b1d-10c0.zip"), new byte[1024]);
        return cache;
    }

    /// <summary>What Yarn keeps beside its cache, and Yarn 1's folders beside Berry's.</summary>
    private IReadOnlyList<string> PopulateNeighbours()
    {
        string[] files =
        [
            Path.Combine(GlobalFolder, "store", "v1", "0a", "index.js"),
            Path.Combine(GlobalFolder, "metadata", "npm", "a1b2c3", "registry.yarnpkg.com", "lodash.json"),
            Path.Combine(GlobalFolder, "index", "0a", "0a1b.dat"),
            Path.Combine(GlobalFolder, "telemetry.json"),
            Path.Combine(YarnFolder, "Data", "global", "package.json"),
            Path.Combine(YarnFolder, "Cache", "v6", "npm-left-pad-1.3.0-abc", "index.js"),
        ];

        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{}");
        }

        return files;
    }

    [Fact]
    public async Task ReportsNotPresentWhereNeitherYarnNorItsCacheIsThere()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>#450: Yarn is asked which it is and about its global folder as the pass begins, and the plan awaits those answers.</summary>
    [Fact]
    public async Task AsksYarnAboutItsGlobalFolderAsThePassBegins()
    {
        PopulateCache();
        var runner = YarnAnswering();

        await EarlyStartProof.StartsBeforeItPlansAsync(
            CreateProvider(runner), runner, "-v", "config get globalFolder", "config get enableCacheClean");
    }

    [Fact]
    public async Task StartsNothingAsThePassBeginsWhenYarnIsNotInstalled()
    {
        PopulateCache();
        var runner = new FakeProcessRunner();

        await EarlyStartProof.StartsNothingAsync(CreateProvider(runner), runner);
    }

    /// <summary>
    /// The common Corepack machine: the yarn on the PATH is Yarn 1 outside any project, and projects run
    /// Yarn 4. Berry's default cache is still found, and Yarn 1 is asked nothing past its version.
    /// </summary>
    [Fact]
    public async Task PlansTheDefaultCacheWholeWhereYarnOneIsOnThePath()
    {
        PopulateCache();
        var runner = YarnAnswering("1.22.22");

        var plan = await CreateProvider(runner).PlanAsync();

        var step = Assert.IsType<DeleteDirectoryStep>(Assert.Single(plan.Steps));
        Assert.Equal(Cache, step.Path);
        Assert.Equal(5120, step.EstimatedBytes);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(["-v"], runner.Invocations.Select(call => call.Arguments));
        Assert.Contains(plan.Notes, n => n.Message.Contains("Yarn 1.22.22", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsPresentWhereOnlyTheCacheIsThere()
    {
        PopulateCache();

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    [Theory]
    [InlineData(Archive)]
    [InlineData("@types-node-npm-20.11.5-0f3a9c2b1d-10c0.zip")]
    [InlineData("react-npm-18.2.0-rc.0-1a2b3c4d5e-10.zip")]
    [InlineData("lodash-npm-4.17.21-6382451519-8.zip")]
    [InlineData("lodash-npm-4.17.21-6382451519-8c9.zip")]
    [InlineData("lodash-npm-4.17.21-6382451519-7.zip")]
    [InlineData("lodash-npm-4.17.21-6382451519-7.zip.flock")]
    [InlineData("lodash-npm-4.17.21-6382451519-10c0.zip-0123456789abcdef.tmp")]
    [InlineData("lodash-npm-4.17.21-6382451519-a1b2c3d4e5.zip")]
    [InlineData("my-pkg-workspace-1a2b3c4d5e-10c0.zip")]
    public void RecognisesTheFilesBerryWritesIntoItsCache(string name) =>
        Assert.True(YarnBerryCache.IsCacheFile(name));

    [Theory]
    [InlineData(".gitignore")]
    [InlineData("notes.txt")]
    [InlineData("lodash.zip")]
    [InlineData("lodash-npm-4.17.21-6382451519-10c0.zip.bak")]
    [InlineData("lodash-npm-4.17.21-638245151-10c0.zip")]
    [InlineData("lodash-npm-4.17.21-6382451519-10c0.zip-0123.tmp")]
    [InlineData("lodash-npm-4.17.21-6382451519-10C0.zip")]
    public void RecognisesNothingElse(string name) =>
        Assert.False(YarnBerryCache.IsCacheFile(name));

    /// <summary>§5.2: the cache goes whole, so one entry Yarn does not write there leaves all of it.</summary>
    [Theory]
    [InlineData("notes.txt", false)]
    [InlineData(".gitignore", false)]
    [InlineData("offline-mirror", true)]
    [InlineData("left-pad-npm-1.3.0-0a1b2c3d4e-10c0.zip", true)]
    public async Task LeavesTheCacheAloneWhereItHoldsSomethingYarnDoesNotWrite(string name, bool isFolder)
    {
        PopulateCache();
        var stranger = Path.Combine(Cache, name);

        if (isFolder)
        {
            Directory.CreateDirectory(stranger);
        }
        else
        {
            File.WriteAllText(stranger, "mine");
        }

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains(name, StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Cache, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LeavesACacheThatIsALinkAlone()
    {
        var elsewhere = PopulateCache(_temp.CreateDirectory("elsewhere"));
        Directory.CreateDirectory(GlobalFolder);
        Junction.ToDirectory(Cache, elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(elsewhere, Archive)));
    }

    /// <summary>A link named like an archive is still a link, and the removal would go through it.</summary>
    [Fact]
    public async Task LeavesTheCacheAloneWhereAnArchiveIsALink()
    {
        PopulateCache();
        var elsewhere = _temp.CreateDirectory("elsewhere");
        File.WriteAllText(Path.Combine(elsewhere, "mine.txt"), "mine");
        Junction.ToDirectory(Path.Combine(Cache, "left-pad-npm-1.3.0-0a1b2c3d4e-10c0.zip"), elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(File.Exists(Path.Combine(elsewhere, "mine.txt")));
    }

    /// <summary>A file link is a file to a listing, so only its attributes say it is not Yarn's archive.</summary>
    [Fact]
    public async Task LeavesTheCacheAloneWhereAnArchiveIsAFileLink()
    {
        PopulateCache();
        var outside = Path.Combine(_temp.CreateDirectory("elsewhere"), "mine.zip");
        File.WriteAllText(outside, "mine");
        SymbolicLink.ToFile(Path.Combine(Cache, "left-pad-npm-1.3.0-0a1b2c3d4e-10c0.zip"), outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("left-pad-npm", StringComparison.Ordinal));
    }

    /// <summary>
    /// A global folder that will not be listed hides what is beside the cache, so §5.6 could assert
    /// none of it: the cache is left, and the row says Windows refused.
    /// </summary>
    [Fact]
    public async Task LeavesTheCacheAloneWhereTheGlobalFolderWillNotBeListed()
    {
        PopulateNeighbours();
        PopulateCache();
        using var denied = new DeniedDirectory(GlobalFolder);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasUnreadableRoot);
    }

    [Fact]
    public async Task LeavesAGlobalFolderThatIsALinkAlone()
    {
        var elsewhere = _temp.CreateDirectory("elsewhere");
        PopulateCache(Path.Combine(elsewhere, "cache"));
        Directory.CreateDirectory(YarnFolder);
        Junction.ToDirectory(GlobalFolder, elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6: the cache goes, and the global folder, everything beside the cache and Yarn 1's folders
    /// outlive it, each asserted.
    /// </summary>
    [Fact]
    public async Task RemovesOnlyTheCacheAndVerifiesEverythingBesideItSurvived()
    {
        var neighbours = PopulateNeighbours();
        PopulateCache();
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();

        foreach (var sibling in new[] { "store", "metadata", "index", "telemetry.json" })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(Path.Combine(GlobalFolder, sibling), StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(GlobalFolder, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.Equal(5120, result.BytesReclaimed);
        Assert.False(Directory.Exists(Cache));
        Assert.True(Directory.Exists(GlobalFolder));
        Assert.All(neighbours, file => Assert.True(File.Exists(file), file));
    }

    /// <summary>
    /// The cache goes whole, so something put into it between the preview and the clean holds the
    /// step back rather than going with it.
    /// </summary>
    [Fact]
    public async Task HoldsTheCleanBackWhereTheCacheGainedAStrangerAfterThePreview()
    {
        PopulateCache();
        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        Assert.Single(plan.Steps);

        var stranger = Path.Combine(Cache, "offline-mirror", "mine.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(stranger)!);
        File.WriteAllText(stranger, "mine");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(stranger));
        Assert.True(File.Exists(Path.Combine(Cache, Archive)));
        Assert.Equal(0, result.BytesReclaimed);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Yarn 1's row protects Berry's whole folder. Run together, this row's removal of the cache inside
    /// it is the run's own, so neither row's negative reads it as an over-reach.
    /// </summary>
    [Fact]
    public async Task RunsBesideYarnOnesRowWithBothNegativesHolding()
    {
        PopulateCache();
        var classicRoot = Path.Combine(YarnFolder, "Cache");
        Directory.CreateDirectory(Path.Combine(classicRoot, "v6", "npm-left-pad-1.3.0-abc"));
        File.WriteAllBytes(Path.Combine(classicRoot, "v6", "npm-left-pad-1.3.0-abc", "index.js"), new byte[2048]);

        _environment.WithExecutable("yarn");
        var classicRunner = new FakeProcessRunner().Replying(Yarn, arguments =>
        {
            switch (arguments)
            {
                case "-v":
                    return new CommandOutcome(0, "1.22.22\n", string.Empty);
                case "cache dir":
                    return new CommandOutcome(0, Path.Combine(classicRoot, "v6") + "\n", string.Empty);
            }

            Directory.Delete(classicRoot, recursive: true);
            Directory.CreateDirectory(Path.Combine(classicRoot, "v6"));
            return new CommandOutcome(0, "success Cleared cache.\n", string.Empty);
        });

        var classic = new YarnClassicCacheProvider(_environment, classicRunner, FakeProcessInspector.NothingRunning, system: _system);
        var berry = CreateProvider(YarnAnswering("1.22.22"));
        var classicPlan = await classic.PlanAsync();
        var berryPlan = await berry.PlanAsync();
        var reach = RunReach.Of([classicPlan, berryPlan]);
        var residue = new RunResidue();

        foreach (var (provider, plan) in new (ICleanupProvider, CleanupPlan)[] { (berry, berryPlan), (classic, classicPlan) })
        {
            var result = await provider.ExecuteAsync(plan, reach, residue);
            Assert.True(result.Verification!.Passed, $"{provider.Name}: {result.Verification.Summary}");
        }

        Assert.False(Directory.Exists(Cache));
        Assert.True(Directory.Exists(GlobalFolder));
    }

    /// <summary>
    /// A global folder Berry reports elsewhere is planned beside the default one, because a project's
    /// own settings can still use the default.
    /// </summary>
    [Fact]
    public async Task PlansTheGlobalFolderBerryReportsBesideTheDefault()
    {
        var moved = Path.Combine(_temp.Path, "caches", "berry");
        PopulateCache();
        PopulateCache(Path.Combine(moved, "cache"));

        var plan = await CreateProvider(YarnAnswering(globalFolder: moved)).PlanAsync();

        Assert.Equal(
            [Cache, Path.Combine(moved, "cache")],
            plan.Steps.OfType<DeleteDirectoryStep>().Select(step => step.Path));
    }

    [Fact]
    public async Task DeclinesAReportedGlobalFolderThatIsTheProfileAndKeepsTheDefault()
    {
        PopulateCache();
        PopulateCache(Path.Combine(_environment.UserProfile, "cache"));

        var plan = await CreateProvider(YarnAnswering(globalFolder: _environment.UserProfile)).PlanAsync();

        Assert.Equal([Cache], plan.Steps.OfType<DeleteDirectoryStep>().Select(step => step.Path));
        Assert.Contains(plan.Notes, n => n.Message.Contains(_environment.UserProfile, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OffersNothingWhereYarnIsSetNotToCleanItsCache()
    {
        PopulateCache();

        var plan = await CreateProvider(YarnAnswering(cleanEnabled: "false")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("enableCacheClean", StringComparison.Ordinal));
    }

    /// <summary>Yarn before 4.6 has no such setting, fails the question, and its cache is offered.</summary>
    [Theory]
    [InlineData("3.8.7")]
    [InlineData("2.4.3")]
    public async Task PlansTheCacheOfABerryThatDoesNotKnowTheCleanSetting(string version)
    {
        PopulateCache();

        var plan = await CreateProvider(YarnAnswering(version, cleanEnabled: null)).PlanAsync();

        Assert.Equal(Cache, Assert.IsType<DeleteDirectoryStep>(Assert.Single(plan.Steps)).Path);
    }

    [Fact]
    public async Task SaysSoAndKeepsTheDefaultWhereYarnWillNotSayItsVersion()
    {
        PopulateCache();
        _environment.WithExecutable("yarn");
        var runner = new FakeProcessRunner().Responding(Yarn, "-v", string.Empty, exitCode: 1);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Single(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("did not say which version", StringComparison.Ordinal));
    }

    [Fact]
    public void ExploreRefusesEverythingInTheGlobalFolderButTheCacheAndItsArchives()
    {
        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(GlobalFolder).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(GlobalFolder, "store")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(GlobalFolder, "metadata")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Cache, "notes.txt")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Cache, "offline-mirror")).IsAllowed);

        // A folder named like an archive is not one: Berry writes files there and nothing else.
        var archiveNamedFolder = Path.Combine(Cache, "left-pad-npm-1.3.0-0a1b2c3d4e-10c0.zip");
        Directory.CreateDirectory(archiveNamedFolder);
        Assert.False(policy.MayRemove(archiveNamedFolder).IsAllowed);

        // The cache is a declared root, and a root is never Explore's target: its archives go one by one.
        Assert.False(policy.MayRemove(Cache).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(Cache, Archive)).IsAllowed);
    }

    [Fact]
    public async Task CleanedPlacesCoverTheCachesThePlanRemoves()
    {
        var moved = Path.Combine(_temp.Path, "caches", "berry");
        PopulateCache();
        PopulateCache(Path.Combine(moved, "cache"));

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider(YarnAnswering(globalFolder: moved))));
    }
}
