using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Berry's answer about where its global folder is outlives a pass for what the row declares, while
/// everything Yarn read to give it is unchanged (#447). The plan deletes by path under that folder
/// (§5.2), so it asks Berry afresh every time, whatever the memory holds.
///
/// <para>Each "asks again" case starts from trusted answers (<see cref="TrustAsync"/>), so only the
/// change it names can explain the ask.</para>
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

    /// <summary>Asks where the row cleans until which Yarn runs and Berry's settings are both trusted.</summary>
    private Task TrustAsync(YarnBerryCacheProvider provider) =>
        _yarn.TrustAsync(
            async () =>
            {
                provider.InvalidateCaches();
                await provider.CleanedPlacesAsync();
            },
            _yarn.VersionKey,
            _yarn.BerryKey);

    /// <summary>
    /// Three passes over what the row declares, each asking which Yarn runs and Berry's settings: a
    /// kept answer would have been confirmed by the second and served to the third.
    /// </summary>
    private async Task AssertNeverKeptAsync(YarnBerryCacheProvider provider)
    {
        await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        await provider.CleanedPlacesAsync();

        Assert.Equal(3, _yarn.Asked("-v"));
        Assert.Equal(3, Asked);
        Assert.False(_yarn.Memory.Holds(_yarn.VersionKey));
        Assert.False(_yarn.Memory.Holds(_yarn.BerryKey));
    }

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
    public async Task APassOnceTheAnswersAreConfirmedWithNothingChangedStartsNoQueryForWhatTheRowDeclares()
    {
        var provider = Provider();

        await provider.CleanedPlacesAsync();
        Assert.False(_yarn.Memory.Holds(_yarn.BerryKey));
        provider.InvalidateCaches();
        await provider.CleanedPlacesAsync();
        Assert.True(_yarn.Memory.Holds(_yarn.VersionKey));
        Assert.True(_yarn.Memory.Holds(_yarn.BerryKey));
        provider.InvalidateCaches();
        var places = await provider.CleanedPlacesAsync();
        var roots = await provider.DiscoverToolRootsAsync();

        Assert.Equal(2, Asked);
        Assert.Equal(2, _yarn.Asked("-v"));
        Assert.Contains(places, p => p.Path == Path.Combine(_first, "cache"));
        Assert.Contains(roots, r => r.Path == _first);
    }

    /// <summary>
    /// A Yarn 1 on the PATH reads <c>yarn-path</c> from <c>~\.yarnrc</c> and runs the script it names.
    /// When that is a Berry, Berry reports no script, so nothing could notice the script replaced: which
    /// Yarn ran and Berry's settings, which depend on it, are both asked again on each pass.
    /// </summary>
    [Fact]
    public async Task ABerryAnswerIsNotKeptWhileAYarn1RcFileCouldHaveHandedYarnOn()
    {
        _yarn.Write(".yarnrc");

        await AssertNeverKeptAsync(Provider());
    }

    /// <summary>
    /// Yarn 1 also reads <c>yarnPath</c> from a <c>.yarnrc.yml</c>. Where <c>YARN_RC_FILENAME</c> has
    /// Berry read another name, Berry does not report that one either.
    /// </summary>
    [Fact]
    public async Task ABerryAnswerIsNotKeptWhileAYarnrcYmlBerryDoesNotReadCouldHaveHandedYarnOn()
    {
        _yarn.Environment.WithEnvironmentVariable("YARN_RC_FILENAME", ".berryrc.yml");
        _yarn.Write(".yarnrc.yml");

        await AssertNeverKeptAsync(Provider());
    }

    /// <summary>Both settings are asked together, so a miss costs one round of waiting.</summary>
    [Fact]
    public async Task AsksTheSettingsTogether()
    {
        var held = new HeldRuns(together: 2);
        _yarn.Runner.Holding(_yarn.Yarn, "globalFolder", held).Holding(_yarn.Yarn, "enableCacheClean", held);

        await Provider().PlanAsync();

        Assert.True(held.StartedTogether);
    }

    /// <summary>
    /// The script answers <c>yarn -v</c> in the program's place. Yarn 1.22 is remembered; a Yarn 4
    /// release copied over the script, with every rc file unchanged, asks which Yarn runs again, so this
    /// row reads Berry's settings and declares the global folder they name.
    /// </summary>
    [Fact]
    public async Task ReplacingTheScriptAsksWhichYarnAgain()
    {
        _yarn.Version = "1.22.22";
        var provider = Provider();
        await TrustYarn1Async(provider);
        Assert.Empty(await provider.DiscoverToolRootsAsync());

        _yarn.ReplaceScript("4.18.1");
        provider.InvalidateCaches();
        var roots = await provider.DiscoverToolRootsAsync();

        Assert.Equal(1, _yarn.Asked("-v"));
        Assert.Equal(1, Asked);
        Assert.Contains(roots, r => r.Path == _first);
    }

    /// <summary>
    /// The plan deletes by path under a folder Berry's settings name, so it asks which Yarn runs afresh
    /// too: a remembered Yarn 1 would skip those settings, and the cache Berry now keeps would go unseen.
    /// </summary>
    [Fact]
    public async Task ThePlanAsksWhichYarnAfreshEvenWhenTheMemoryHoldsIt()
    {
        _yarn.Version = "1.22.22";
        var provider = Provider();
        await TrustYarn1Async(provider);

        _yarn.Version = "4.18.1";
        var plan = await provider.PlanAsync();

        Assert.Equal(1, _yarn.Asked("-v"));
        Assert.Equal([Path.Combine(_first, "cache")], plan.Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path));
    }

    /// <summary>
    /// Explore builds a fresh set of providers for each policy. The first build holds Yarn's answers
    /// and the second confirms them; a third is answered from the memory they filled, and still refuses
    /// the global folder Berry reported and everything in it beside the cache.
    /// </summary>
    [Fact]
    public async Task AnExploreBuildOnceTheAnswersAreConfirmedAnswersFromMemoryWithoutAskingYarn()
    {
        var global = Path.Combine(Environment.UserProfile, "berry-global");
        YarnToolFixture.WriteAt(global, "cache", Archive);
        YarnToolFixture.WriteAt(global, "store", "v1", "index.js");
        _yarn.BerryGlobalFolder = global;

        var first = await ExploreActionPolicy.ForAsync(_yarn.System, Environment, new FakeVolumeInventory(), [Provider()]);
        await ExploreActionPolicy.ForAsync(_yarn.System, Environment, new FakeVolumeInventory(), [Provider()]);
        Assert.True(_yarn.Memory.Holds(_yarn.VersionKey));
        Assert.True(_yarn.Memory.Holds(_yarn.BerryKey));
        Assert.Equal(2, Asked);

        var third = await ExploreActionPolicy.ForAsync(_yarn.System, Environment, new FakeVolumeInventory(), [Provider()]);

        Assert.Equal(2, Asked);
        Assert.Equal(2, _yarn.Asked("-v"));
        Assert.False(first.MayRemove(global).IsAllowed);
        Assert.False(third.MayRemove(global).IsAllowed);
        Assert.Contains("This is the global folder of Yarn 2", first.MayRemove(Path.Combine(global, "store")).Reason, StringComparison.Ordinal);
        Assert.Contains("This is the global folder of Yarn 2", third.MayRemove(Path.Combine(global, "store")).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DeclaredInputs))]
    public async Task ChangingADeclaredInputAsksAgain(string input)
    {
        var provider = Provider();

        await TrustAsync(provider);
        provider.InvalidateCaches();
        _yarn.Change(input);
        await provider.CleanedPlacesAsync();

        Assert.Equal(1, Asked);
    }

    [Theory]
    [InlineData("AppData\\Roaming\\npm\\node_modules\\yarn\\package.json")]
    [InlineData("nodejs\\node.exe")]
    [InlineData("project\\.yarn\\releases\\yarn.cjs")]
    public async Task DeletingADeclaredInputAsksAgain(string input)
    {
        var provider = Provider();

        await TrustAsync(provider);
        provider.InvalidateCaches();
        File.Delete(Path.Combine(Environment.UserProfile, input));
        await provider.CleanedPlacesAsync();

        Assert.Equal(1, Asked);
    }

    /// <summary>A renamed rc file is read in place of <c>.yarnrc.yml</c>, so writing it asks again.</summary>
    [Theory]
    [InlineData("custom.yml")]
    [InlineData("AppData\\Local\\Deguffer\\tool-working-directory\\custom.yml")]
    public async Task ChangingTheRcFileYarnRcFilenameNamesAsksAgain(string input)
    {
        Environment.WithEnvironmentVariable("YARN_RC_FILENAME", "custom.yml");
        var provider = Provider();

        await TrustAsync(provider);

        provider.InvalidateCaches();
        _yarn.Change(input);
        await provider.CleanedPlacesAsync();

        Assert.Equal(1, Asked);
    }

    [Fact]
    public async Task AVariableSetInTheEnvironmentYarnIsStartedWithAsksAgain()
    {
        var provider = Provider();

        await TrustAsync(provider);
        provider.InvalidateCaches();
        Environment.WithToolEnvironmentVariable("YARN_GLOBAL_FOLDER", Path.Combine(_yarn.Temp, "elsewhere"));
        await provider.CleanedPlacesAsync();

        Assert.Equal(1, Asked);
    }

    [Fact]
    public async Task ReplacingTheExecutableAsksAgain()
    {
        var provider = Provider();

        await TrustAsync(provider);
        provider.InvalidateCaches();
        _yarn.ReplaceYarn();
        await provider.CleanedPlacesAsync();

        Assert.Equal(1, Asked);
        Assert.Equal(1, _yarn.Asked("-v"));
    }

    /// <summary>
    /// Explore builds a fresh set of providers for each policy. Once the answers are confirmed, another
    /// asks nothing, because they are kept by the memory of the runner they share, not by the provider
    /// that asked.
    /// </summary>
    [Fact]
    public async Task ASecondProviderOnTheSameMemoryAnswersWithoutAsking()
    {
        await TrustAsync(Provider());
        var second = Provider();
        var roots = await second.DiscoverToolRootsAsync();
        var places = await second.CleanedPlacesAsync();

        Assert.Equal(0, Asked);
        Assert.Equal(0, _yarn.Asked("-v"));
        Assert.Contains(roots, r => r.Path == _first);
        Assert.Contains(places, p => p.Path == Path.Combine(_first, "cache"));
    }

    /// <summary>
    /// Both rows ask which Yarn runs through one question, declaring the same inputs, so the answer Yarn
    /// 1's plan holds is the one what Berry's row declares confirms, and neither row starts
    /// <c>yarn -v</c> again. Berry's plan asks afresh, which the test above covers.
    /// </summary>
    [Fact]
    public async Task BothRowsShareOneVersionQuestion()
    {
        _yarn.Version = "1.22.22";
        _yarn.ClassicRoot = Path.Combine(Environment.LocalAppData, "Yarn", "Cache");
        var classic = new YarnClassicCacheProvider(
            Environment, _yarn.Runner, FakeProcessInspector.NothingRunning, system: _yarn.System, memory: _yarn.Memory);
        var berry = Provider();

        await classic.PlanAsync();
        Assert.False(_yarn.Memory.Holds(_yarn.VersionKey));
        await berry.CleanedPlacesAsync();
        Assert.True(_yarn.Memory.Holds(_yarn.VersionKey));
        classic.InvalidateCaches();
        berry.InvalidateCaches();
        await berry.DiscoverToolRootsAsync();
        await classic.PlanAsync();

        Assert.Equal(2, _yarn.Asked("-v"));
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

    /// <summary>
    /// The failure comes after trusted answers, from an ask replacing Yarn forced, so a failure kept as
    /// an answer would read the same inputs before and after and be served to the next pass.
    /// </summary>
    [Fact]
    public async Task AFailedLocationQueryIsNotKept()
    {
        var provider = Provider();
        await TrustAsync(provider);

        _yarn.ReplaceYarn();
        _yarn.LocationFails = true;
        provider.InvalidateCaches();
        await provider.CleanedPlacesAsync();
        Assert.False(_yarn.Memory.Holds(_yarn.BerryKey));
        provider.InvalidateCaches();
        await provider.CleanedPlacesAsync();

        Assert.Equal(2, Asked);
    }

    /// <summary>
    /// A failed script question leaves a script in use that no input names: which Yarn runs and the
    /// global folder are used and asked on every pass. The failed answers name no script, unlike the
    /// trusted ones before them, so the second failed pass is the first a kept answer could be
    /// confirmed by, and the third the first it could be served to.
    /// </summary>
    [Fact]
    public async Task AFailedScriptQueryIsUsedButNotKept()
    {
        var provider = Provider();
        await TrustAsync(provider);

        _yarn.ReplaceYarn();
        _yarn.ScriptFails = true;
        provider.InvalidateCaches();
        var failed = await provider.CleanedPlacesAsync();
        provider.InvalidateCaches();
        await provider.CleanedPlacesAsync();
        Assert.False(_yarn.Memory.Holds(_yarn.VersionKey));
        Assert.False(_yarn.Memory.Holds(_yarn.BerryKey));
        provider.InvalidateCaches();
        await provider.CleanedPlacesAsync();

        Assert.Contains(failed, p => p.Path == Path.Combine(_first, "cache"));
        Assert.Equal(3, Asked);
        Assert.Equal(3, _yarn.Asked("-v"));
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
        await TrustAsync(provider);

        var moved = Path.Combine(_yarn.Temp, "caches", "berry-moved");
        YarnToolFixture.WriteAt(moved, "cache", Archive);
        _yarn.BerryGlobalFolder = moved;

        var plan = await provider.PlanAsync();

        Assert.Equal(1, Asked);
        Assert.Equal([Path.Combine(moved, "cache")], plan.Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path));
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.StartsWith(_first, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Asks where the row cleans until which Yarn runs is trusted, for a Yarn 1 on the PATH, where the
    /// row asks Berry's settings nothing.
    /// </summary>
    private Task TrustYarn1Async(YarnBerryCacheProvider provider) =>
        _yarn.TrustAsync(
            async () =>
            {
                provider.InvalidateCaches();
                await provider.DiscoverToolRootsAsync();
            },
            _yarn.VersionKey);

    private YarnBerryCacheProvider Provider() =>
        new(Environment, _yarn.Runner, FakeProcessInspector.NothingRunning, system: _yarn.System, memory: _yarn.Memory);
}
