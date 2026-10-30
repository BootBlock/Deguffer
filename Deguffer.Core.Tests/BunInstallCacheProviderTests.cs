using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.1 through <c>prune</c> alone. No Bun was installed on the machine these were written on, so the
/// replies are Bun 1.4.4's as its source prints them, and the prune removes what its own walk removes.
/// </summary>
public sealed class BunInstallCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public BunInstallCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private string Bun => _environment.FindExecutable("bun")!;

    private string Home => Path.Combine(_environment.UserProfile, ".bun");

    private string Cache => Path.Combine(Home, "install", "cache");

    private BunInstallCacheProvider CreateProvider(FakeProcessRunner runner) =>
        new(_environment, runner, FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>
    /// A bun on the PATH that says it is <paramref name="version"/>, reports <paramref name="cache"/>
    /// with no line ending, and prunes as 1.4.4 does: every package folder goes, and the rest stays.
    /// </summary>
    private FakeProcessRunner BunAnswering(string version = "1.4.4", string? cache = null)
    {
        _environment.WithExecutable("bun");
        cache ??= Cache;

        return new FakeProcessRunner().Replying(Bun, arguments =>
        {
            switch (arguments)
            {
                case "--version":
                    return new CommandOutcome(0, version + "\n", string.Empty);
                case "pm cache":
                    return new CommandOutcome(0, cache, string.Empty);
            }

            Assert.Equal($"pm cache prune --max-age 0 --cache-dir \"{cache}\"", arguments);

            foreach (var package in BunCacheEntries.PackagesIn(cache)!)
            {
                Directory.Delete(package, recursive: true);
            }

            return new CommandOutcome(0, "Removed 2 packages older than 0 days (8 KB)\n", string.Empty);
        });
    }

    /// <summary>
    /// A cache holding one of each kind of entry, returning the package folders prune removes and the
    /// entries it keeps.
    /// </summary>
    private (string[] Packages, string[] Kept) PopulateCache()
    {
        string[] packages =
        [
            Write(4096, "left-pad@1.3.0@@@1", "index.js"),
            Write(4096, "@types", "node@20.0.0@@@1", "index.d.ts"),
        ];

        string[] kept =
        [
            Write(2048, "links", "left-pad@1.3.0-abc", "index.js"),
            Write(1024, "0123456789abcdef.git", "HEAD"),
            Write(1024, "@t@", "transpiled.js"),
            Write(1024, ".tmp", "partial"),
            Write(512, "0123456789abcdef.npm"),
            Write(512, "left-pad", "1.3.0"),
        ];

        return ([.. packages.Select(file => Path.GetDirectoryName(file)!)], kept);
    }

    private string Write(int bytes, params string[] segments)
    {
        var path = Path.Combine([Cache, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task ReportsNotPresentWhenBunIsNotInstalled()
    {
        var provider = CreateProvider(new FakeProcessRunner());

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task PlansBunsPruneSentToTheCacheItMeasured()
    {
        var (packages, _) = PopulateCache();

        var plan = await CreateProvider(BunAnswering()).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal(Bun, command.FileName);
        Assert.Equal($"pm cache prune --max-age 0 --cache-dir \"{Cache}\"", command.Arguments);
        Assert.Equal(packages.Order(StringComparer.OrdinalIgnoreCase), command.MeasuredPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(8192, command.EstimatedBytes);
        Assert.True(plan.CountsLinksFromElsewhere);
    }

    /// <summary>
    /// Before 1.4.4 Bun's only eviction is <c>rm</c>, which deletes the <c>links</c> store. Such a Bun
    /// is asked its version and nothing else.
    /// </summary>
    [Theory]
    [InlineData("1.4.3")]
    [InlineData("1.3.14")]
    [InlineData("0.9.0")]
    public async Task OffersNothingForABunWithoutPrune(string version)
    {
        PopulateCache();
        var runner = BunAnswering(version);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("1.4.4", StringComparison.Ordinal));
        Assert.Equal(["--version"], runner.Invocations.Select(call => call.Arguments));
    }

    [Theory]
    [InlineData("1.5.0")]
    [InlineData("2.0.1")]
    public async Task PrunesWithALaterBun(string version)
    {
        PopulateCache();

        var plan = await CreateProvider(BunAnswering(version)).PlanAsync();

        Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.4")]
    [InlineData("bun 1.4.4")]
    public async Task OffersNothingWhereBunWillNotSayItsVersion(string version)
    {
        var plan = await CreateProvider(BunAnswering(version)).PlanAsync();

        Assert.True(plan.WasNotExamined);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public async Task OffersNothingWhereTheCacheHoldsNoPackage()
    {
        Write(2048, "links", "left-pad@1.3.0-abc", "index.js");

        var plan = await CreateProvider(BunAnswering()).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    /// <summary>
    /// A cache that is the account's temporary folder holds what other rows remove. It holds only
    /// packages here, so nothing but the folder check can decline it.
    /// </summary>
    public async Task LeavesACacheThatIsATemporaryFolderAlone()
    {
        PopulateCache();
        _environment.WithTempPath(Cache);

        var plan = await CreateProvider(BunAnswering()).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("temporary", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6: run as Bun 1.4.4 runs it, the prune takes the package folders, and the links store and
    /// everything else prune keeps outlive it.
    /// </summary>
    [Fact]
    public async Task VerifiesTheLinksStoreAndWhatPruneKeepsSurvived()
    {
        var (packages, kept) = PopulateCache();
        var provider = CreateProvider(BunAnswering());

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(Cache, "links"), StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);
        var verification = await provider.VerifyAsync(plan);

        Assert.Equal(8192, result.BytesReclaimed);
        Assert.True(verification.Passed);
        Assert.All(packages, package => Assert.False(Directory.Exists(package)));
        Assert.All(kept, file => Assert.True(File.Exists(file), $"{file} did not survive."));
    }

    /// <summary>A link with a package's name is not a directory to prune, so it is neither measured nor offered.</summary>
    [Fact]
    public async Task NeverMeasuresALinkWithAPackagesName()
    {
        var (packages, _) = PopulateCache();
        var elsewhere = _temp.CreateDirectory("elsewhere");
        Junction.ToDirectory(Path.Combine(Cache, "linked@1.0.0@@@1"), elsewhere);

        var plan = await CreateProvider(BunAnswering()).PlanAsync();
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal(packages.Order(StringComparer.OrdinalIgnoreCase), command.MeasuredPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("left-pad@1.3.0@@@1", true, false, "Package")]
    [InlineData("@G@0123abcd", true, false, "Package")]
    [InlineData("@GH@owner-repo-0123abc@@@1", true, false, "Package")]
    [InlineData("@T@sha512-abc@@@1", true, false, "Package")]
    [InlineData("@types", true, false, "Scope")]
    [InlineData("node@20.0.0@@@1", true, true, "Package")]
    [InlineData("@nested@1.0.0", true, true, "Other")]
    [InlineData("left-pad", true, false, "Index")]
    [InlineData("links", true, false, "Other")]
    [InlineData("0123456789abcdef.git", true, false, "Other")]
    [InlineData("@t@", true, false, "Other")]
    [InlineData(".tmp", true, false, "Other")]
    [InlineData("left-pad@1.3.0@@@1", false, false, "Other")]
    public void ClassifiesEntriesAsPruneDoes(string name, bool isDirectory, bool inScope, string expected)
    {
        Assert.Equal(expected, BunCacheEntries.Classify(name, isDirectory, inScope).ToString());
    }

    [Fact]
    public void ExploreRefusesTheLinksStoreAndBunsHome()
    {
        var policy = new ExploreActionPolicy([], CreateProvider(new FakeProcessRunner()).ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(Home).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Home, "install", "global")).IsAllowed);
        Assert.False(policy.MayRemove(Cache).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Cache, "links")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Cache, "0123456789abcdef.git")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(Cache, "left-pad@1.3.0@@@1")).IsAllowed);
    }

    [Fact]
    public async Task CleanedPlacesCoverTheCacheThePruneIsSentTo()
    {
        PopulateCache();

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider(BunAnswering())));
    }
}
