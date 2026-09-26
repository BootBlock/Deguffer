using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Battle.net launcher's own cache. Nothing is enumerated, so there is no unrecognised child to
/// classify, and what has to be shown is that a rule reaching into the launcher's folder cannot
/// reach the account data and the database beside the cache.
/// </summary>
public sealed class BattleNetCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly BattleNetFixture _battleNet;

    public BattleNetCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _battleNet = new BattleNetFixture(_environment.LocalAppData);
    }

    public void Dispose() => _temp.Dispose();

    private BattleNetCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    [Fact]
    public async Task ReportsNotPresentWhenTheLauncherHasNeverRunHere()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// The launcher's folder also holds what this row never removes, so the folder existing must not
    /// read as presence — that would report the row and then plan nothing.
    /// </summary>
    [Fact]
    public async Task TheLaunchersFolderExistingIsNotPresence()
    {
        BattleNetFixture.Populate(_battleNet.Account, "account.db");
        BattleNetFixture.Populate(_battleNet.Logs, "battle.net.log");

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// §3. The cache is filled again from Blizzard's servers, which is what Tier 1 asks, so the row
    /// is ticked for the user and needs no typed phrase.
    /// </summary>
    [Fact]
    public async Task PlansTheCacheAloneAtTier1AndPreSelectsIt()
    {
        _battleNet.CreateMeasuredLayout();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(_battleNet.Cache, Assert.Single(plan.TargetedPaths));
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal(8192, plan.EstimatedBytes);

        var finding = new Finding(provider, IsPresent: true, plan);

        Assert.True(finding.IsPreSelectedByDefault);
        Assert.NotEqual(ConfirmationLevel.TypedPhrase, ConfirmationRequirement.For(plan).Level);
    }

    /// <summary>
    /// §5.6 against the siblings, proved by running a plan. The account data and the database are the
    /// consequential ones, and the database is a file, so nothing that classifies a directory would
    /// ever have checked it. A neighbour the declaration never names is here too: it is unreachable
    /// by construction, which only a run shows.
    /// </summary>
    [Fact]
    public async Task TheLaunchersOwnDataSurvivesARunAndIsAssertedRatherThanMerelyOmitted()
    {
        _battleNet.CreateMeasuredLayout();
        var unnamed = BattleNetFixture.Populate(Path.Combine(_battleNet.Launcher, "Updates"), "pending.bin");
        var browserMarker = Path.Combine(_battleNet.BrowserCaches, BattleNetFixture.Marker);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        string[] asserted =
        [
            _battleNet.Launcher, _battleNet.Account, _battleNet.Database, _battleNet.BrowserCaches,
            browserMarker, _battleNet.Logs,
        ];

        foreach (var path in asserted)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(_battleNet.Cache));

        Assert.All(
            [_battleNet.Launcher, _battleNet.Account, _battleNet.BrowserCaches, _battleNet.Common, _battleNet.Logs, unnamed],
            path => Assert.True(Directory.Exists(path), $"{path} went with the cache."));
        Assert.True(File.Exists(_battleNet.Database), "the launcher's database went with the cache.");
        Assert.True(File.Exists(browserMarker), "the built-in browser's settings went with the cache.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.6 has to fail loudly, or it is decoration.</summary>
    [Fact]
    public async Task VerificationFailsLoudlyIfTheAccountDataVanished()
    {
        _battleNet.CreateMeasuredLayout();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(_battleNet.Account, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(_battleNet.Account, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A declared path reached by name has none of the protection an enumeration gives. A junctioned
    /// cache would be deleted through while every survivor resolved through the same link and passed.
    /// </summary>
    [Fact]
    public async Task AJunctionedCacheIsNamedRatherThanFollowed()
    {
        var outside = BattleNetFixture.Populate(Path.Combine(_temp.Path, "elsewhere"), "irreplaceable.bin");

        Directory.CreateDirectory(_battleNet.Launcher);
        SymbolicLink.ToDirectory(_battleNet.Cache, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(outside, "irreplaceable.bin")), "a junctioned cache was deleted through.");
    }

    /// <summary>The same rule one level up, where the launcher's whole folder has been moved.</summary>
    [Fact]
    public async Task AJunctionedLauncherFolderIsNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var cache = BattleNetFixture.Populate(Path.Combine(outside, "Cache", "0a"), "0a000000000000000000000000000001");

        SymbolicLink.ToDirectory(_battleNet.Launcher, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(cache), "planning looked through a junctioned launcher folder.");
    }

    /// <summary>§5.3, named by the launcher's own process.</summary>
    [Fact]
    public async Task WarnsWhileTheLauncherIsRunning()
    {
        _battleNet.CreateMeasuredLayout();

        var plan = await CreateProvider(new FakeProcessInspector("Battle.net")).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("Battle.net", StringComparison.Ordinal));
    }

    /// <summary>
    /// The declaration pinned by name. A second location added without a test is a directory nobody
    /// decided to delete, and the survivors are the ones somebody chose.
    /// </summary>
    [Fact]
    public void TheDeclarationIsTheOnePathAndNothingElse()
    {
        var root = Assert.Single(CreateProvider().Roots);

        Assert.Equal(_battleNet.Launcher, root.Path);
        Assert.False(root.RequiresElevation);

        var location = Assert.Single(root.Locations);
        Assert.Equal(_battleNet.Cache, Path.Combine(root.Path, location.RelativePath));
        Assert.Equal(DeclaredLocationKind.Directory, location.Kind);

        Assert.Equal(
            ["Account", "CachedData.db", "BrowserCaches", @"BrowserCaches\LocalPrefs.json", "Logs"],
            root.ProtectedNames.Select(p => p.RelativePath));
    }

    /// <summary>
    /// §7's age column stays blank. The cache's children are hash buckets, so its newest write says
    /// "today" about a cache that is mostly old.
    /// </summary>
    [Fact]
    public async Task ReportsNoAgeForACacheWhoseOneDateWouldMeanNothing()
    {
        _battleNet.CreateMeasuredLayout();

        var plan = await CreateProvider().PlanAsync();

        Assert.Null(Assert.Single(plan.Steps).LastWritten);
    }

    /// <summary>
    /// §7.1 reads the shared declaration: the cache and the logs may be removed from the Storage
    /// page, and the launcher's folder, its account data and its browser may not.
    /// </summary>
    [Theory]
    [InlineData("Cache", true)]
    [InlineData("Logs", true)]
    [InlineData("Account", false)]
    [InlineData("BrowserCaches", false)]
    [InlineData("SomethingNew", false)]
    [InlineData("", false)]
    public void ExploreReadsTheLaunchersFolderFromTheDeclaration(string relative, bool allowed)
    {
        _battleNet.CreateMeasuredLayout();
        Directory.CreateDirectory(Path.Combine(_battleNet.Launcher, "SomethingNew"));

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? _battleNet.Launcher : Path.Combine(_battleNet.Launcher, relative)).IsAllowed);
    }

    /// <summary>
    /// Both rows declare the same root, so dropping either one leaves Explore still unwilling to
    /// remove the launcher's account data.
    /// </summary>
    [Fact]
    public void DeclaresTheSameLauncherFolderRootAsTheLogProvider()
    {
        var root = Assert.Single(CreateProvider().ToolRoots);
        var fromLogs = Assert.Single(new BattleNetLogProvider(_environment).ToolRoots);

        Assert.Equal(_battleNet.Launcher, root.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(root.Path, fromLogs.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(root.Reason, fromLogs.Reason, StringComparer.Ordinal);

        foreach (var name in new[] { "Cache", "Logs", "Account", "BrowserCaches", "CachedData.db" })
        {
            Assert.Equal(root.Recognises(name), fromLogs.Recognises(name));
        }
    }
}
