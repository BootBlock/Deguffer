using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Berry's answer about where its global folder is outlives a pass for what the row declares, while
/// everything Yarn read to give it is unchanged (#447). The plan deletes by path under that folder
/// (§5.2), so it asks Berry afresh every time, whatever the memory holds.
/// </summary>
public sealed class YarnBerryCacheProviderMemoryTests : IDisposable
{
    private const string Archive = "lodash-npm-4.17.21-6382451519-10c0.zip";

    private readonly YarnToolFixture _yarn = new();
    private readonly string _first;

    public YarnBerryCacheProviderMemoryTests()
    {
        _first = Path.Combine(_yarn.Temp, "caches", "berry-first");
        YarnToolFixture.WriteAt(_first, "cache", Archive);
        _yarn.Version = "4.18.1";
        _yarn.BerryGlobalFolder = _first;
    }

    public void Dispose() => _yarn.Dispose();

    private FakeUserEnvironment Environment => _yarn.Environment;

    private int Asked => _yarn.Asked("config get globalFolder --json");

    private static string WorkingFolder => Path.Combine("AppData", "Local", "Deguffer", "tool-working-directory");

    /// <summary>Every input Berry's answer is declared to depend on, relative to the fake profile.</summary>
    public static TheoryData<string> DeclaredInputs() =>
    [
        Path.Combine("AppData", "Roaming", "npm", "node.exe"),
        Path.Combine("nodejs", "node.exe"),
        Path.Combine("AppData", "Roaming", "npm", "node_modules", "yarn", "package.json"),
        Path.Combine("AppData", "Roaming", "package.json"),
        Path.Combine("AppData", "Local", "node", "corepack", "yarn", "yarn-4.5.0.tgz"),
        Path.Combine(WorkingFolder, "package.json"),
        Path.Combine(WorkingFolder, ".corepack.env"),
        Path.Combine(WorkingFolder, ".yarnrc.yml"),
        Path.Combine(WorkingFolder, "yarn.lock"),
        Path.Combine("AppData", "Local", ".yarnrc.yml"),
        Path.Combine("AppData", "Local", "yarn.lock"),
        ".yarnrc.yml",
        "package.json",
        Path.Combine("project", ".yarn", "releases", "yarn.cjs"),
    ];

    [Fact]
    public async Task ASecondPassAfterInvalidationWithNothingChangedStartsNoQueryForWhatTheRowDeclares()
    {
        var provider = Provider();

        await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        var places = await provider.CleanedPlacesAsync();
        var roots = await provider.DiscoverToolRootsAsync();

        Assert.Equal(1, Asked);
        Assert.Equal(1, _yarn.Asked("-v"));
        Assert.Contains(places, p => p.Path == Path.Combine(_first, "cache"));
        Assert.Contains(roots, r => r.Path == _first);
    }

    /// <summary>The three settings are asked together, so a miss costs one round of waiting.</summary>
    [Fact]
    public async Task AsksTheSettingsTogether()
    {
        var held = new HeldRuns(together: 3);
        _yarn.Runner.Holding(_yarn.Yarn, "--json", held);

        await Provider().PlanAsync();

        Assert.True(held.StartedTogether);
    }

    [Theory]
    [MemberData(nameof(DeclaredInputs))]
    public async Task ChangingADeclaredInputAsksAgain(string input)
    {
        var provider = Provider();

        await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        _yarn.Change(input);
        await provider.CleanedPlacesAsync();

        Assert.Equal(2, Asked);
    }

    [Theory]
    [InlineData("AppData\\Roaming\\npm\\node_modules\\yarn\\package.json")]
    [InlineData("nodejs\\node.exe")]
    [InlineData("project\\.yarn\\releases\\yarn.cjs")]
    public async Task DeletingADeclaredInputAsksAgain(string input)
    {
        var provider = Provider();

        await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        File.Delete(Path.Combine(Environment.UserProfile, input));
        await provider.CleanedPlacesAsync();

        Assert.Equal(2, Asked);
    }

    /// <summary>A renamed rc file is read in place of <c>.yarnrc.yml</c>, so writing it asks again.</summary>
    [Theory]
    [InlineData("custom.yml")]
    [InlineData("AppData\\Local\\Deguffer\\tool-working-directory\\custom.yml")]
    public async Task ChangingTheRcFileYarnRcFilenameNamesAsksAgain(string input)
    {
        Environment.WithEnvironmentVariable("YARN_RC_FILENAME", "custom.yml");
        var provider = Provider();

        await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        await provider.CleanedPlacesAsync();
        Assert.Equal(1, Asked);

        provider.InvalidateCaches();
        _yarn.Change(input);
        await provider.CleanedPlacesAsync();

        Assert.Equal(2, Asked);
    }

    [Fact]
    public async Task AVariableSetInTheEnvironmentYarnIsStartedWithAsksAgain()
    {
        var provider = Provider();

        await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        Environment.WithToolEnvironmentVariable("YARN_GLOBAL_FOLDER", Path.Combine(_yarn.Temp, "elsewhere"));
        await provider.CleanedPlacesAsync();

        Assert.Equal(2, Asked);
    }

    [Fact]
    public async Task ReplacingTheExecutableAsksAgain()
    {
        var provider = Provider();

        await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        _yarn.ReplaceYarn();
        await provider.CleanedPlacesAsync();

        Assert.Equal(2, Asked);
        Assert.Equal(2, _yarn.Asked("-v"));
    }

    /// <summary>
    /// Explore builds a fresh set of providers for each policy. The second asks nothing, because the
    /// answer is kept by the memory of the runner they share, not by the provider that asked.
    /// </summary>
    [Fact]
    public async Task ASecondProviderOnTheSameMemoryAnswersWithoutAsking()
    {
        await Provider().DiscoverToolRootsAsync();
        var second = Provider();
        var roots = await second.DiscoverToolRootsAsync();
        var places = await second.CleanedPlacesAsync();

        Assert.Equal(1, Asked);
        Assert.Equal(1, _yarn.Asked("-v"));
        Assert.Contains(roots, r => r.Path == _first);
        Assert.Contains(places, p => p.Path == Path.Combine(_first, "cache"));
    }

    /// <summary>Both rows ask which Yarn runs through one question, so a pass of both starts <c>yarn -v</c> once.</summary>
    [Fact]
    public async Task BothRowsShareOneVersionQuestion()
    {
        _yarn.Version = "1.22.22";
        _yarn.ClassicRoot = Path.Combine(Environment.LocalAppData, "Yarn", "Cache");
        var classic = new YarnClassicCacheProvider(
            Environment, _yarn.Runner, FakeProcessInspector.NothingRunning, system: _yarn.System, memory: _yarn.Memory);
        var berry = Provider();

        await classic.PlanAsync();
        await berry.PlanAsync();
        classic.InvalidateCaches();
        berry.InvalidateCaches();
        await berry.PlanAsync();
        await classic.PlanAsync();

        Assert.Equal(1, _yarn.Asked("-v"));
    }

    /// <summary>
    /// §5.6 when the global folder moved between two passes: the second plan removes the new cache and
    /// protects the new global folder and everything beside its cache, touches nothing in the old one,
    /// and the run leaves all of that standing.
    /// </summary>
    [Fact]
    public async Task APlanAfterTheGlobalFolderMovedTargetsAndProtectsTheNewFolderAndNotTheOld()
    {
        var provider = Provider();
        Assert.Contains(Path.Combine(_first, "cache"), (await provider.PlanAsync()).Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path));

        var moved = Path.Combine(_yarn.Temp, "caches", "berry-moved");
        YarnToolFixture.WriteAt(moved, "cache", Archive);
        var store = YarnToolFixture.WriteAt(moved, "store", "v1", "index.js");
        var unrecognised = YarnToolFixture.WriteAt(moved, "unrecognised.bin");
        _yarn.Change(".yarnrc.yml");
        _yarn.BerryGlobalFolder = moved;
        provider.InvalidateCaches();

        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(moved, "cache")], plan.Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == moved);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(moved, "store"));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(moved, "unrecognised.bin"));
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.StartsWith(_first, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(Path.Combine(moved, "cache")));
        Assert.True(File.Exists(store));
        Assert.True(File.Exists(unrecognised));
        Assert.True(File.Exists(Path.Combine(_first, "cache", Archive)));
    }

    [Fact]
    public async Task AFailedLocationQueryIsNotKept()
    {
        _yarn.LocationFails = true;
        var provider = Provider();

        await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        await provider.CleanedPlacesAsync();

        Assert.Equal(2, Asked);
    }

    /// <summary>
    /// A failed script question leaves a script in use that no input names: the global folder is
    /// used and asked again next time.
    /// </summary>
    [Fact]
    public async Task AFailedScriptQueryIsUsedButNotKept()
    {
        _yarn.ScriptFails = true;
        var provider = Provider();

        var first = await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        await provider.CleanedPlacesAsync();

        Assert.Contains(first, p => p.Path == Path.Combine(_first, "cache"));
        Assert.Equal(2, Asked);
    }

    /// <summary>
    /// The plan deletes by path under the global folder, so it asks Berry now even where the memory
    /// holds an answer whose inputs are unchanged, and a folder Berry moved without any declared input
    /// changing is the one it removes from.
    /// </summary>
    [Fact]
    public async Task ThePlanAsksAfreshEvenWhenTheMemoryHoldsTheAnswer()
    {
        var provider = Provider();
        await provider.DiscoverToolRootsAsync();
        Assert.True(_yarn.Memory.Holds($"config get globalFolder enableCacheClean yarnPath --json|{_yarn.Yarn}"));

        var moved = Path.Combine(_yarn.Temp, "caches", "berry-moved");
        YarnToolFixture.WriteAt(moved, "cache", Archive);
        _yarn.BerryGlobalFolder = moved;

        var plan = await provider.PlanAsync();

        Assert.Equal(2, Asked);
        Assert.Equal([Path.Combine(moved, "cache")], plan.Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path));
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.StartsWith(_first, StringComparison.OrdinalIgnoreCase));
    }

    private YarnBerryCacheProvider Provider() =>
        new(Environment, _yarn.Runner, FakeProcessInspector.NothingRunning, system: _yarn.System, memory: _yarn.Memory);
}
