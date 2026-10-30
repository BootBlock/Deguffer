using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// ccache is a §5.1 provider whose reason for the command is the configuration file that can sit
/// inside its cache, so most of these are about asking ccache rather than assuming, and about that
/// file surviving.
///
/// <para>Everything runs against a synthetic profile through <see cref="FakeUserEnvironment"/>. No
/// ccache was installed on the machine these were written on.</para>
/// </summary>
public sealed class CcacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public CcacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>The ccache the fake environment resolves, so a test names the program a step must run.</summary>
    private string Ccache => _environment.FindExecutable("ccache")!;

    private string DefaultRoot => Path.Combine(_environment.LocalAppData, "ccache");

    private string LegacyRoot => Path.Combine(_environment.UserProfile, ".ccache");

    private CcacheProvider CreateProvider(FakeProcessRunner runner, IProcessInspector? inspector = null) =>
        new(_environment, runner, inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>ccache installed and answering where its cache is.</summary>
    private FakeProcessRunner CcacheReporting(string cache)
    {
        _environment.WithExecutable("ccache");
        return new FakeProcessRunner().Responding(Ccache, "--get-config cache_dir", cache + "\n");
    }

    /// <summary>ccache installed, and too old to answer (it has no <c>--get-config</c>).</summary>
    private FakeProcessRunner CcacheNotAnswering()
    {
        _environment.WithExecutable("ccache");
        return new FakeProcessRunner().Responding(Ccache, "--get-config", string.Empty, exitCode: 1);
    }

    /// <summary>A cache with two shards and its configuration file, as ccache writes them.</summary>
    private static string CreateCache(string root)
    {
        foreach (var shard in (string[])["0", "e"])
        {
            Directory.CreateDirectory(Path.Combine(root, shard, "3"));
            File.WriteAllBytes(Path.Combine(root, shard, "3", "entryR"), new byte[4096]);
        }

        File.WriteAllText(Path.Combine(root, CcacheProvider.ConfigurationFile), "max_size = 20G\n");
        return root;
    }

    [Fact]
    public async Task ReportsNotPresentWhenCcacheIsNotInstalled()
    {
        CreateCache(DefaultRoot);
        var provider = CreateProvider(new FakeProcessRunner());

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task ClearsWithCcachesOwnCommandRatherThanDeletingThePath()
    {
        var cache = CreateCache(DefaultRoot);

        var plan = await CreateProvider(CcacheReporting(cache)).PlanAsync();

        // §5.1: nothing is targeted for deletion; the tool is asked to clear.
        Assert.Empty(plan.TargetedPaths);
        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(Ccache, step.FileName);
        Assert.Equal("--clear", step.Arguments);
        Assert.Equal([cache], step.MeasuredPaths);
        Assert.True(step.EstimatedBytes > 0);
    }

    [Fact]
    public async Task AsksCcacheWhereItsCacheIsRatherThanAssuming()
    {
        CreateCache(DefaultRoot);
        var elsewhere = CreateCache(Path.Combine(_temp.Path, "caches", "ccache"));
        var runner = CcacheReporting(elsewhere);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(runner.Invocations, i => i.Arguments == "--get-config cache_dir");
        Assert.Equal([elsewhere], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("ccache reports", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FallsBackToTheDocumentedLocationWhenCcacheCannotAnswer()
    {
        var cache = CreateCache(DefaultRoot);

        var plan = await CreateProvider(CcacheNotAnswering()).PlanAsync();

        Assert.Equal([cache], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("documented location", StringComparison.Ordinal));
    }

    /// <summary>
    /// ccache's own rule: <c>%USERPROFILE%\.ccache</c> wins wherever it exists, so the newer default is
    /// not assumed.
    /// </summary>
    [Fact]
    public async Task FallsBackToTheLegacyFolderWhereItExists()
    {
        CreateCache(DefaultRoot);
        var legacy = CreateCache(LegacyRoot);

        var plan = await CreateProvider(CcacheNotAnswering()).PlanAsync();

        Assert.Equal([legacy], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
    }

    [Fact]
    public async Task FallsBackToTheCacheVariableBeforeEitherDefault()
    {
        CreateCache(LegacyRoot);
        var moved = CreateCache(Path.Combine(_temp.Path, "caches", "ccache"));
        _environment.WithEnvironmentVariable(CcacheProvider.CacheVariable, moved);

        var plan = await CreateProvider(CcacheNotAnswering()).PlanAsync();

        Assert.Equal([moved], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
    }

    /// <summary>
    /// The reason this provider runs a command. The configuration file in the cache, and the cache
    /// folder holding it, are what the run has to prove it left standing (§5.6).
    /// </summary>
    [Fact]
    public async Task AssertsTheConfigurationAndTheCacheFolderSurvive()
    {
        var cache = CreateCache(DefaultRoot);
        var configuration = Path.Combine(cache, CcacheProvider.ConfigurationFile);

        var plan = await CreateProvider(CcacheReporting(cache)).PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(configuration, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>
    /// §5.6, executed. A clear that behaves as ccache's does empties the shards, and the configuration is
    /// still there afterwards.
    /// </summary>
    [Fact]
    public async Task ExecutingKeepsTheConfigurationAndPassesVerification()
    {
        var cache = CreateCache(DefaultRoot);
        var runner = CcacheReporting(cache).Replying(Ccache, arguments =>
        {
            if (arguments != "--clear")
            {
                return null;
            }

            foreach (var shard in Directory.EnumerateDirectories(cache))
            {
                Directory.Delete(shard, recursive: true);
            }

            return new CommandOutcome(0, string.Empty, string.Empty);
        });

        var provider = CreateProvider(runner);
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(cache, CcacheProvider.ConfigurationFile)));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.6's negative: a clear that took the configuration with it fails verification loudly.</summary>
    [Fact]
    public async Task VerificationFailsLoudlyIfTheConfigurationVanished()
    {
        var cache = CreateCache(DefaultRoot);
        var configuration = Path.Combine(cache, CcacheProvider.ConfigurationFile);

        var provider = CreateProvider(CcacheReporting(cache));
        var plan = await provider.PlanAsync();

        File.Delete(configuration);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(configuration, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ccache's clear is sent wherever ccache is set to keep its cache, so a cache set to the profile
    /// would have it clearing folders that are not ccache's. Nothing is run, and the plan says why.
    /// </summary>
    [Fact]
    public async Task WillNotRunTheClearWhereCcacheIsSetToUseTheProfile()
    {
        var plan = await CreateProvider(CcacheReporting(_environment.UserProfile)).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("will not run ccache's clear", StringComparison.Ordinal));
    }

    /// <summary>
    /// The declined folder is still where ccache writes its configuration, so Explore refuses that file
    /// there and nothing else.
    /// </summary>
    [Fact]
    public async Task ADeclinedCacheFolderRefusesOnlyTheConfigurationInExplore()
    {
        var provider = CreateProvider(CcacheReporting(_environment.UserProfile));

        var root = Assert.Single(
            await provider.DiscoverToolRootsAsync(),
            root => root.Path.Equals(_environment.UserProfile, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ToolRootClaim.NamedEntries, root.Claim);
        Assert.False(root.Recognises(new ToolRootChild(CcacheProvider.ConfigurationFile, ChildKind.File)));
        Assert.True(root.Recognises(new ToolRootChild("Documents", ChildKind.Folder)));
        Assert.DoesNotContain(
            await provider.CleanedPlacesAsync(),
            place => place.Path.Equals(_environment.UserProfile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReResolvesTheCacheAfterInvalidationBecauseItCanMove()
    {
        var first = CreateCache(DefaultRoot);
        var moved = CreateCache(Path.Combine(_temp.Path, "caches", "ccache"));
        var runner = CcacheReporting(first);
        var provider = CreateProvider(runner);

        Assert.Equal([first], Assert.Single((await provider.PlanAsync()).Steps.OfType<RunCommandStep>()).MeasuredPaths);

        runner.Responding(Ccache, "--get-config cache_dir", moved);
        provider.InvalidateCaches();

        Assert.Equal([moved], Assert.Single((await provider.PlanAsync()).Steps.OfType<RunCommandStep>()).MeasuredPaths);
    }

    [Fact]
    public async Task SaysSoWhenCcacheIsInstalledButHasNeverCachedAnything()
    {
        var plan = await CreateProvider(CcacheReporting(DefaultRoot)).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("cached nothing yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WarnsWhenCcacheIsRunning()
    {
        var cache = CreateCache(DefaultRoot);

        var plan = await CreateProvider(CcacheReporting(cache), new FakeProcessInspector("ccache")).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task CleanedPlacesCoverTheDefaultCacheTheCommandIsSentTo()
    {
        CreateCache(DefaultRoot);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider(CcacheNotAnswering())));
    }

    [Fact]
    public async Task CleanedPlacesCoverTheCacheCcacheReportsElsewhere()
    {
        var elsewhere = CreateCache(Path.Combine(_temp.Path, "caches", "ccache"));

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider(CcacheReporting(elsewhere))));
    }

    /// <summary>
    /// Explore may take a shard on its own, and never the folder, the configuration, or the folders a
    /// running compile writes in.
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData(CcacheProvider.ConfigurationFile, false)]
    [InlineData("tmp", false)]
    [InlineData("lock", false)]
    [InlineData("0", true)]
    [InlineData("f", true)]
    public void ExploreMayRemoveOnlyACacheShard(string relative, bool allowed)
    {
        var policy = new ExploreActionPolicy([], CreateProvider(new FakeProcessRunner()).ToolRoots, new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(relative.Length == 0 ? DefaultRoot : Path.Combine(DefaultRoot, relative)).IsAllowed);
    }
}
