using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.1 for Yarn 1 alone. No Yarn was installed on the machine these were written on, so the replies
/// are Yarn's as its source prints them: <c>-v</c> the bare version, <c>cache dir</c> the versioned
/// folder, and a clean that deletes the root and makes an empty <c>v6</c> again.
/// </summary>
public sealed class YarnClassicCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public YarnClassicCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private string Yarn => _environment.FindExecutable("yarn")!;

    private string YarnFolder => Path.Combine(_environment.LocalAppData, "Yarn");

    private string Root => Path.Combine(YarnFolder, "Cache");

    private YarnClassicCacheProvider CreateProvider(FakeProcessRunner runner) =>
        new(_environment, runner, FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>
    /// A yarn on the PATH that says it is <paramref name="version"/>, keeps its cache in
    /// <paramref name="root"/>, and cleans as Yarn 1 does: the root goes, and an empty <c>v6</c> comes back.
    /// </summary>
    private FakeProcessRunner YarnAnswering(string version = "1.22.22", string? root = null, string? reported = null)
    {
        _environment.WithExecutable("yarn");
        root ??= Root;

        return new FakeProcessRunner().Replying(Yarn, arguments =>
        {
            switch (arguments)
            {
                case "-v":
                    return new CommandOutcome(0, version + "\n", string.Empty);
                case "cache dir":
                    return new CommandOutcome(0, (reported ?? Path.Combine(root, "v6")) + "\n", string.Empty);
            }

            Assert.Equal($"cache clean --cache-folder \"{root}\" --non-interactive", arguments);
            Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(Path.Combine(root, "v6"));
            return new CommandOutcome(0, "yarn cache v1.22.22\nsuccess Cleared cache.\nDone in 0.50s.\n", string.Empty);
        });
    }

    private void PopulateCache(string? root = null)
    {
        root ??= Root;
        Directory.CreateDirectory(Path.Combine(root, "v6", "npm-left-pad-1.3.0-abc", "node_modules", "left-pad"));
        File.WriteAllBytes(Path.Combine(root, "v6", "npm-left-pad-1.3.0-abc", "node_modules", "left-pad", "index.js"), new byte[4096]);
        Directory.CreateDirectory(Path.Combine(root, "v6", ".tmp"));
        Directory.CreateDirectory(Path.Combine(root, "v4"));
        File.WriteAllBytes(Path.Combine(root, "v4", "old.tgz"), new byte[1024]);
    }

    private string PopulateData()
    {
        var global = Path.Combine(YarnFolder, "Data", "global", "package.json");
        Directory.CreateDirectory(Path.GetDirectoryName(global)!);
        File.WriteAllText(global, "{}");
        Directory.CreateDirectory(Path.Combine(YarnFolder, "Berry", "cache"));
        return global;
    }

    [Fact]
    public async Task ReportsNotPresentWhenYarnIsNotInstalled()
    {
        var provider = CreateProvider(new FakeProcessRunner());

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task PlansYarnsCleanSentToTheRootItMeasured()
    {
        PopulateCache();

        var plan = await CreateProvider(YarnAnswering()).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal(Yarn, command.FileName);
        Assert.Equal($"cache clean --cache-folder \"{Root}\" --non-interactive", command.Arguments);
        Assert.Equal([Root], command.MeasuredPaths);
        Assert.True(command.EstimatedBytes >= 5120);
        Assert.Equal(CancelledCommand.Finishes, command.WhenCancelled);
    }

    /// <summary>
    /// Yarn 2 and later answer to the same name, and their clean deletes the cache Plug'n'Play projects
    /// run from. A Yarn that is not 1 is never asked anything further.
    /// </summary>
    [Theory]
    [InlineData("4.5.0")]
    [InlineData("2.4.3")]
    [InlineData("0.27.5")]
    [InlineData("1.22.22-rc.1")]
    public async Task NeverCleansAYarnThatIsNotYarnOne(string version)
    {
        PopulateCache();
        var runner = YarnAnswering(version);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Equal(["-v"], runner.Invocations.Select(call => call.Arguments));
    }

    [Fact]
    public async Task OffersNothingWhereYarnWillNotSayItsVersion()
    {
        _environment.WithExecutable("yarn");
        var runner = new FakeProcessRunner().Responding(Yarn, "-v", string.Empty, exitCode: 1);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.WasNotExamined);
        Assert.Empty(plan.Steps);
    }

    [Theory]
    [InlineData(@"relative\Cache\v6")]
    [InlineData(@"C:\Users\testuser\AppData\Local\Yarn\Cache")]
    public async Task OffersNothingWhereTheReportedCacheIsNotAVersionFolder(string reported)
    {
        PopulateCache();

        var plan = await CreateProvider(YarnAnswering(reported: reported)).PlanAsync();

        Assert.True(plan.WasNotExamined);
        Assert.Empty(plan.Steps);
    }

    /// <summary>A cache-folder set to Yarn's own folder would have the clean delete Data and Berry's cache with it.</summary>
    [Fact]
    public async Task LeavesARootThatHoldsYarnsDataAlone()
    {
        PopulateData();
        PopulateCache(YarnFolder);

        var plan = await CreateProvider(YarnAnswering(root: YarnFolder)).PlanAsync();

        Assert.Empty(plan.Steps);
    }

    [Fact]
    public async Task LeavesARootSetToTheProfileAlone()
    {
        var plan = await CreateProvider(YarnAnswering(root: _environment.UserProfile)).PlanAsync();

        Assert.Empty(plan.Steps);
    }

    /// <summary>§5.2: the clean deletes the whole root, so anything there Yarn does not make stops it.</summary>
    [Theory]
    [InlineData("notes.txt", false)]
    [InlineData("offline-mirror", true)]
    [InlineData("v6.bak", true)]
    public async Task LeavesTheRootAloneWhereItHoldsSomethingYarnDoesNotMake(string name, bool isFolder)
    {
        PopulateCache();
        var stranger = Path.Combine(Root, name);

        if (isFolder)
        {
            Directory.CreateDirectory(stranger);
        }
        else
        {
            File.WriteAllText(stranger, "mine");
        }

        var plan = await CreateProvider(YarnAnswering()).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains(name, StringComparison.Ordinal));
    }

    /// <summary>§5.6: run as Yarn 1 runs it, the clean takes the root, and Data and Berry's cache outlive it.</summary>
    [Fact]
    public async Task VerifiesYarnsDataAndBerrysCacheSurvivedTheClean()
    {
        var global = PopulateData();
        PopulateCache();
        var provider = CreateProvider(YarnAnswering());

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(YarnFolder, "Data"), StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(YarnFolder, "Berry"), StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);
        var verification = await provider.VerifyAsync(plan);

        Assert.True(result.BytesReclaimed >= 5120);
        Assert.True(verification.Passed);
        Assert.True(File.Exists(global));
        Assert.True(Directory.Exists(Path.Combine(YarnFolder, "Berry", "cache")));
    }

    [Fact]
    public void ExploreRefusesYarnsDataAndBerrysCache()
    {
        var policy = new ExploreActionPolicy([], CreateProvider(new FakeProcessRunner()).ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(YarnFolder).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(YarnFolder, "Data")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(YarnFolder, "Berry")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Root, "offline-mirror")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_environment.UserProfile, ".yarnrc.yml")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(Root, "v6")).IsAllowed);
    }

    [Fact]
    public async Task CleanedPlacesCoverTheRootTheCleanIsSentTo()
    {
        var elsewhere = Path.Combine(_temp.Path, "caches", "yarn");
        PopulateCache(elsewhere);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider(YarnAnswering(root: elsewhere))));
    }
}
