using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Battle.net launcher's logs: Tier 3, because a removed log is not written again, and in the
/// same folder as the account data the cache row must also leave standing.
/// </summary>
public sealed class BattleNetLogProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly BattleNetFixture _battleNet;

    public BattleNetLogProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _battleNet = new BattleNetFixture(_environment.LocalAppData);
    }

    public void Dispose() => _temp.Dispose();

    private BattleNetLogProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    [Fact]
    public async Task ReportsNotPresentWhenTheLauncherHasWrittenNoLog()
    {
        BattleNetFixture.Populate(Path.Combine(_battleNet.Cache, "0a"), "0a000000000000000000000000000001");

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// §3. A removed log is not written again, so the row is Tier 3: never ticked for the user, and
    /// behind the confirmation the tier asks for.
    /// </summary>
    [Fact]
    public async Task PlansTheLogsAloneAtTier3AndLeavesTheRowUnticked()
    {
        _battleNet.CreateMeasuredLayout();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(_battleNet.Logs, Assert.Single(plan.TargetedPaths));
        Assert.Equal(SafetyTier.UserData, plan.Tier);
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
    }

    /// <summary>
    /// §7's age column. A log is appended to, which moves the file and leaves the folder alone, so
    /// the age has to come from inside the folder or a log written this minute reads as months old.
    /// </summary>
    [Fact]
    public async Task TheRowCarriesTheNewestWriteInsideTheFolder()
    {
        var logs = BattleNetFixture.Populate(_battleNet.Logs, "battle.net.log");
        var log = Path.Combine(logs, "battle.net.log");

        TempDirectory.Age(log, TimeSpan.FromDays(400));
        Directory.SetLastWriteTimeUtc(LongPath.Extended(logs), DateTime.UtcNow.AddDays(-400));
        File.WriteAllBytes(log, new byte[8192]);

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps);

        Assert.NotNull(step.LastWritten);
        Assert.True(step.LastWritten > DateTime.UtcNow.AddDays(-1), "the age came from the folder rather than the log.");
    }

    /// <summary>
    /// §5.6 against the siblings, proved by running a plan. The launcher's cache is the other row's
    /// subject, and this row must leave it standing as surely as the account data.
    /// </summary>
    [Fact]
    public async Task TheLaunchersOwnDataSurvivesARunAndIsAssertedRatherThanMerelyOmitted()
    {
        _battleNet.CreateMeasuredLayout();
        var browserMarker = Path.Combine(_battleNet.BrowserCaches, BattleNetFixture.Marker);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        string[] asserted =
        [
            _battleNet.Launcher, _battleNet.Account, _battleNet.Database, _battleNet.BrowserCaches,
            browserMarker, _battleNet.Cache,
        ];

        foreach (var path in asserted)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(_battleNet.Logs));
        Assert.All(
            [_battleNet.Launcher, _battleNet.Account, _battleNet.BrowserCaches, _battleNet.Cache],
            path => Assert.True(Directory.Exists(path), $"{path} went with the logs."));
        Assert.True(File.Exists(_battleNet.Database), "the launcher's database went with the logs.");
        Assert.True(File.Exists(browserMarker), "the built-in browser's settings went with the logs.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A junctioned log folder is named and never deleted through.</summary>
    [Fact]
    public async Task AJunctionedLogFolderIsNamedRatherThanFollowed()
    {
        var outside = BattleNetFixture.Populate(Path.Combine(_temp.Path, "elsewhere"), "irreplaceable.log");

        Directory.CreateDirectory(_battleNet.Launcher);
        SymbolicLink.ToDirectory(_battleNet.Logs, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(outside, "irreplaceable.log")), "a junctioned log folder was deleted through.");
    }

    /// <summary>§5.3: the running launcher holds the log it is writing open.</summary>
    [Fact]
    public async Task WarnsWhileTheLauncherIsRunning()
    {
        _battleNet.CreateMeasuredLayout();

        var plan = await CreateProvider(new FakeProcessInspector("Battle.net")).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("Battle.net", StringComparison.Ordinal));
    }

    /// <summary>The declaration pinned by name, as the cache row's is.</summary>
    [Fact]
    public void TheDeclarationIsTheOnePathAndNothingElse()
    {
        var root = Assert.Single(CreateProvider().Roots);

        Assert.Equal(_battleNet.Launcher, root.Path);
        Assert.Equal(_battleNet.Logs, Path.Combine(root.Path, Assert.Single(root.Locations).RelativePath));
        Assert.Equal(
            ["Account", "CachedData.db", "BrowserCaches", @"BrowserCaches\LocalPrefs.json", "Cache"],
            root.ProtectedNames.Select(p => p.RelativePath));
    }
}
