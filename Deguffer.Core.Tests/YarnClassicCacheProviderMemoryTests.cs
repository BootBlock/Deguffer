using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Yarn 1's answers about which Yarn it is and where its cache is outlive a pass while everything Yarn
/// read to give them is unchanged, and are asked again the moment any of it changes (#447).
///
/// <para>The plan only runs Yarn's own clean (§5.1), sent to a root it vets on the disk, so it plans
/// from the remembered answer: a missed input could misstate the figure. Each declared input still has
/// a case here, because a missed one is a moved cache measured where Yarn no longer keeps it.</para>
/// </summary>
public sealed class YarnClassicCacheProviderMemoryTests : IDisposable
{
    private readonly YarnToolFixture _yarn = new();

    public YarnClassicCacheProviderMemoryTests()
    {
        _yarn.ClassicRoot = Root;
        Populate(Root);
    }

    public void Dispose() => _yarn.Dispose();

    private FakeUserEnvironment Environment => _yarn.Environment;

    private string YarnFolder => Path.Combine(Environment.LocalAppData, "Yarn");

    private string Root => Path.Combine(YarnFolder, "Cache");

    private int Asked => _yarn.Asked("cache dir");

    /// <summary>Every input Yarn 1's answer is declared to depend on, relative to the fake profile.</summary>
    public static TheoryData<string> DeclaredInputs() =>
    [
        Path.Combine("AppData", "Roaming", "npm", "node.exe"),
        Path.Combine("nodejs", "node.exe"),
        Path.Combine("AppData", "Roaming", "npm", "node_modules", "yarn", "package.json"),
        Path.Combine("AppData", "Roaming", "package.json"),
        Path.Combine("AppData", "Local", "node", "corepack", "yarn", "yarn-4.5.0.tgz"),
        Path.Combine(WorkingFolder, "package.json"),
        Path.Combine(WorkingFolder, ".corepack.env"),
        Path.Combine(WorkingFolder, ".yarnrc"),
        Path.Combine(WorkingFolder, ".npmrc"),
        Path.Combine(WorkingFolder, ".yarnrc.yml"),
        Path.Combine("AppData", "Local", ".yarnrc"),
        ".yarnrc",
        ".yarnrc.yml",
        ".npmrc",
        "package.json",
        Path.Combine(".yarn", "config"),
        Path.Combine(".config", "yarn", "config"),
        Path.Combine("AppData", "Local", "Yarn", "Config", "config"),
        Path.Combine("nodejs", "etc", "yarnrc"),
        Path.Combine("nodejs", "etc", "npmrc"),
        Path.Combine("project", ".yarn", "releases", "yarn.cjs"),
    ];

    private static string WorkingFolder => Path.Combine("AppData", "Local", "Deguffer", "tool-working-directory");

    [Fact]
    public async Task ASecondPassAfterInvalidationWithNothingChangedStartsNoQuery()
    {
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        var second = await provider.PlanAsync();

        Assert.Equal(1, Asked);
        Assert.Equal(1, _yarn.Asked("-v"));
        Assert.Equal(1, _yarn.Asked("config get yarn-path"));
        Assert.Equal([Root], Assert.IsType<RunCommandStep>(Assert.Single(second.Steps)).MeasuredPaths);
    }

    /// <summary>Both script settings are asked beside the version, not after it, so a miss costs one round of waiting.</summary>
    [Fact]
    public async Task AsksTheScriptBesideTheVersion()
    {
        var held = new HeldRuns(together: 3);
        _yarn.Runner.Holding(_yarn.Yarn, "-v", held).Holding(_yarn.Yarn, "config get yarn", held);

        await Provider().PlanAsync();

        Assert.True(held.StartedTogether);
    }

    /// <summary>
    /// The script answers <c>yarn -v</c> in the program's place, so a newer release copied over it, with
    /// every rc file unchanged, asks which Yarn runs again, and a Yarn 4 there is never sent Yarn 1's clean.
    /// </summary>
    [Fact]
    public async Task ReplacingTheScriptAsksWhichYarnAgain()
    {
        var provider = Provider();
        await provider.PlanAsync();

        _yarn.ReplaceScript("4.18.1");
        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        Assert.Equal(2, _yarn.Asked("-v"));
        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Yarn 4.18.1, not Yarn 1", StringComparison.Ordinal));
    }

    /// <summary>
    /// Corepack opens a relative <c>COREPACK_HOME</c> from the folder it runs in, so that is the home
    /// stamped: nothing changed there keeps the answer, and a release written there asks again.
    /// </summary>
    [Fact]
    public async Task ARelativeCorepackHomeIsReadFromTheToolsWorkingFolder()
    {
        var home = Path.Combine(ToolInputs.WorkingDirectory(Environment), "relative-corepack");
        Directory.CreateDirectory(Path.Combine(home, "yarn"));
        Environment.WithToolEnvironmentVariable("COREPACK_HOME", "relative-corepack");
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();
        Assert.Equal(1, _yarn.Asked("-v"));

        YarnToolFixture.WriteAt(home, "yarn", "yarn-4.5.0.tgz");
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, _yarn.Asked("-v"));
        Assert.Equal(2, Asked);
    }

    [Theory]
    [MemberData(nameof(DeclaredInputs))]
    public async Task ChangingADeclaredInputAsksAgain(string input)
    {
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        _yarn.Change(input);
        await provider.PlanAsync();

        Assert.Equal(2, Asked);
    }

    [Theory]
    [InlineData("AppData\\Roaming\\npm\\node_modules\\yarn\\package.json")]
    [InlineData("nodejs\\node.exe")]
    [InlineData("project\\.yarn\\releases\\yarn.cjs")]
    public async Task DeletingADeclaredInputAsksAgain(string input)
    {
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        File.Delete(Path.Combine(Environment.UserProfile, input));
        await provider.PlanAsync();

        Assert.Equal(2, Asked);
    }

    /// <summary>
    /// A file a variable names is an input of its own: written while the variable stays the same, it
    /// still asks again.
    /// </summary>
    [Theory]
    [InlineData("YARN_CONFIG", "custom\\yarnrc", "custom\\yarnrc")]
    [InlineData("PREFIX", "prefix", "prefix\\etc\\yarnrc")]
    [InlineData("PREFIX", "prefix", "prefix\\etc\\npmrc")]
    [InlineData("COREPACK_HOME", "corepack", "corepack\\yarn\\yarn-4.5.0.tgz")]
    public async Task ChangingAFileAVariableNamesAsksAgain(string variable, string value, string input)
    {
        Directory.CreateDirectory(Path.Combine(Environment.UserProfile, "corepack", "yarn"));
        Environment.WithEnvironmentVariable(variable, Path.Combine(Environment.UserProfile, value));
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();
        Assert.Equal(1, Asked);

        provider.InvalidateCaches();
        _yarn.Change(input);
        await provider.PlanAsync();

        Assert.Equal(2, Asked);
    }

    [Fact]
    public async Task AVariableSetInTheEnvironmentYarnIsStartedWithAsksAgain()
    {
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        Environment.WithToolEnvironmentVariable("YARN_CACHE_FOLDER", Path.Combine(_yarn.Temp, "elsewhere"));
        await provider.PlanAsync();

        Assert.Equal(2, Asked);
    }

    [Fact]
    public async Task ReplacingTheExecutableAsksAgain()
    {
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        _yarn.ReplaceYarn();
        await provider.PlanAsync();

        Assert.Equal(2, Asked);
        Assert.Equal(2, _yarn.Asked("-v"));
    }

    /// <summary>
    /// Explore builds a fresh set of providers for each policy. The second asks nothing, because the
    /// answers are kept by the memory of the runner they share, not by the provider that asked.
    /// </summary>
    [Fact]
    public async Task ASecondProviderOnTheSameMemoryAnswersWithoutAsking()
    {
        var moved = Path.Combine(_yarn.Temp, "caches", "yarn");
        Populate(moved);
        _yarn.ClassicRoot = moved;

        await Provider().DiscoverToolRootsAsync();
        var second = Provider();
        var roots = await second.DiscoverToolRootsAsync();
        var places = await second.CleanedPlacesAsync();

        Assert.Equal(1, Asked);
        Assert.Equal(1, _yarn.Asked("-v"));
        Assert.Equal(moved, Assert.Single(roots).Path);
        Assert.Contains(places, p => p.Path == moved);
    }

    /// <summary>
    /// Explore builds a fresh set of providers for each policy. The second build is answered from the
    /// memory the first filled, and still refuses the cache root Yarn 1 reported and anything in it
    /// that is not a versioned cache folder.
    /// </summary>
    [Fact]
    public async Task ASecondExploreBuildAnswersFromMemoryWithoutAskingYarn()
    {
        var moved = Path.Combine(Environment.UserProfile, "caches", "yarn");
        Populate(moved);
        var stranger = YarnToolFixture.WriteAt(moved, "stranger.bin");
        _yarn.ClassicRoot = moved;

        var first = await ExploreActionPolicy.ForAsync(_yarn.System, Environment, new FakeVolumeInventory(), [Provider()]);
        var second = await ExploreActionPolicy.ForAsync(_yarn.System, Environment, new FakeVolumeInventory(), [Provider()]);

        Assert.Equal(1, Asked);
        Assert.Equal(1, _yarn.Asked("-v"));
        Assert.False(first.MayRemove(moved).IsAllowed);
        Assert.False(second.MayRemove(moved).IsAllowed);
        Assert.Contains("This is Yarn 1's cache.", first.MayRemove(stranger).Reason, StringComparison.Ordinal);
        Assert.Contains("This is Yarn 1's cache.", second.MayRemove(stranger).Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.6 when the cache moved between two passes: the second plan sends the clean to the new root and
    /// leaves the old one, and the run leaves Yarn's data, Berry's folder and everything beside the
    /// new root standing.
    /// </summary>
    [Fact]
    public async Task APlanAfterTheCacheMovedTargetsAndProtectsTheNewRootAndNotTheOld()
    {
        var data = _yarn.Write("AppData", "Local", "Yarn", "Data", "global", "package.json");
        var berry = _yarn.Write("AppData", "Local", "Yarn", "Berry", "cache", "lodash-npm-4.17.21-6382451519-10c0.zip");
        var oldContent = Path.Combine(Root, "v6", "npm-left-pad-1.3.0-abc", "index.js");
        var provider = Provider();
        await provider.PlanAsync();

        var moved = Path.Combine(_yarn.Temp, "caches", "yarn");
        Populate(moved);
        var sibling = YarnToolFixture.WriteAt(Path.Combine(_yarn.Temp, "caches"), "unrecognised", "keep.bin");
        _yarn.Change(".yarnrc");
        _yarn.ClassicRoot = moved;
        _yarn.OnClean = arguments =>
        {
            Assert.Equal(YarnClassicCache.CleanArguments(moved), arguments);
            Directory.Delete(moved, recursive: true);
            Directory.CreateDirectory(Path.Combine(moved, "v6"));
        };
        provider.InvalidateCaches();

        var plan = await provider.PlanAsync();

        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal(YarnClassicCache.CleanArguments(moved), command.Arguments);
        Assert.Equal([moved], command.MeasuredPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(YarnFolder, "Data"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(YarnFolder, "Berry"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.StartsWith(Root, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, Asked);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.True(File.Exists(oldContent));
        Assert.True(File.Exists(data));
        Assert.True(File.Exists(berry));
        Assert.True(File.Exists(sibling));
        Assert.False(Directory.EnumerateFileSystemEntries(Path.Combine(moved, "v6")).Any());
    }

    [Fact]
    public async Task AFailedLocationQueryIsNotKept()
    {
        _yarn.LocationFails = true;
        var provider = Provider();

        var first = await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.True(first.WasNotExamined);
        Assert.Equal(2, Asked);
    }

    /// <summary>
    /// A failed script question leaves a script in use that no input names: which Yarn runs and the
    /// location are planned from and asked again next time.
    /// </summary>
    [Fact]
    public async Task AFailedScriptQueryIsPlannedFromButNotKept()
    {
        _yarn.ScriptFails = true;
        var provider = Provider();

        var first = await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal([Root], Assert.IsType<RunCommandStep>(Assert.Single(first.Steps)).MeasuredPaths);
        Assert.Equal(2, Asked);
        Assert.Equal(2, _yarn.Asked("-v"));
    }

    [Fact]
    public async Task AFailedVersionQueryIsNotKept()
    {
        var runner = new FakeProcessRunner().Responding(_yarn.Yarn, "-v", string.Empty, exitCode: 1);
        var provider = new YarnClassicCacheProvider(
            Environment, runner, FakeProcessInspector.NothingRunning, system: _yarn.System, memory: _yarn.Memory);

        await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, runner.Invocations.Count(i => i.Arguments == "-v"));
    }

    private YarnClassicCacheProvider Provider() =>
        new(Environment, _yarn.Runner, FakeProcessInspector.NothingRunning, system: _yarn.System, memory: _yarn.Memory);

    private static void Populate(string root)
    {
        YarnToolFixture.WriteAt(root, "v6", "npm-left-pad-1.3.0-abc", "index.js");
    }
}
