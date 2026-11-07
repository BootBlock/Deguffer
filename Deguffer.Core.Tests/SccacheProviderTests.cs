using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// sccache has no command that empties its cache, so these are about what the path-based removal may
/// take, and about the server that has to be stopped before it does.
///
/// <para>Everything runs against a synthetic profile through <see cref="FakeUserEnvironment"/>, and the
/// server is a name in <see cref="FakeProcessInspector"/> that the fake stop command removes. No sccache
/// was installed on the machine these were written on.</para>
/// </summary>
public sealed class SccacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeProcessRunner _runner = new();

    public SccacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private SccacheProvider CreateProvider(IProcessInspector? inspector = null, TimeProvider? time = null) =>
        new(_environment, _runner, inspector ?? FakeProcessInspector.NothingRunning, system: _system, time: time);

    private string Root => Path.Combine(_environment.LocalAppData, "Mozilla", "sccache", "cache");

    private string Child(string name) => Path.Combine(Root, name);

    /// <summary>The sccache the fake environment resolves, so a test names the program a release runs.</summary>
    private string Sccache => _environment.FindExecutable("sccache")!;

    /// <summary>A directory holding one file, so it measures above zero and is selectable.</summary>
    private static string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    /// <summary>Two shards and the preprocessor cache, each holding a file.</summary>
    private void CreateCache()
    {
        foreach (var name in (string[])["0", "a", "preprocessor"])
        {
            Populate(Child(name));
        }
    }

    /// <summary>sccache installed, with its server running until the stop command ends it.</summary>
    private FakeProcessInspector ServerThatStops()
    {
        _environment.WithExecutable("sccache");
        var inspector = new FakeProcessInspector("sccache");

        _runner.Replying(Sccache, arguments =>
        {
            inspector.WithoutRunning("sccache");
            return new CommandOutcome(0, "Stopping sccache server...", string.Empty);
        });

        return inspector;
    }

    [Fact]
    public async Task ReportsNotPresentWhenSccacheHasNeverCached()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// The cache sits in <c>cache</c> beneath sccache's project folder, where the <c>directories</c>
    /// crate puts it, and not in the project folder sccache's documentation names.
    /// </summary>
    [Fact]
    public async Task FindsTheCacheWhereSccacheWritesIt()
    {
        Populate(Path.Combine(_environment.LocalAppData, "Mozilla", "sccache", "0"));

        Assert.False(await CreateProvider().IsPresentAsync());

        Populate(Child("0"));

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansEachShardAndThePreprocessorCacheButNeverTheRoot()
    {
        CreateCache();

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            [Child("0"), Child("a"), Child("preprocessor")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(Root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Root, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>§5.2: a name sccache does not write is Tier 4, left alone and asserted standing.</summary>
    [Fact]
    public async Task AnUnrecognisedChildIsLeftAloneAndAssertedStanding()
    {
        CreateCache();
        var stranger = Populate(Child("ab"));

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(stranger, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(stranger, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("'ab'", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryDeclaredChildIsARegenerableCache()
    {
        Assert.Equal(17, SccacheProvider.Children.DisposableNames.Count());
        Assert.All(
            SccacheProvider.Children.DisposableNames,
            name => Assert.Equal(SafetyTier.RegenerableCache, SccacheProvider.Children.Classify(name).Tier));
        Assert.False(SccacheProvider.Children.IsDisposable("config"));
    }

    [Fact]
    public async Task HonoursTheCacheVariableWhenItNamesAFolderOfItsOwn()
    {
        var moved = Path.Combine(_temp.Path, "caches", "sccache");
        Populate(Path.Combine(moved, "f"));
        _environment.WithEnvironmentVariable(SccacheProvider.CacheVariable, moved);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(moved, "f")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A shard is one hexadecimal character, which says nothing about whose folder it is. A variable
    /// naming the profile would offer whatever 'a' or 'f' is there.
    /// </summary>
    [Fact]
    public async Task WillNotTreatAFolderHoldingTheProfileAsSccaches()
    {
        var shared = Populate(Path.Combine(_environment.UserProfile, "a"));
        _environment.WithEnvironmentVariable(SccacheProvider.CacheVariable, _environment.UserProfile);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.DoesNotContain(provider.ToolRoots, root => root.Path.Equals(_environment.UserProfile, StringComparison.OrdinalIgnoreCase));

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(shared, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("will not treat that as sccache's cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeclinesARootThatIsItselfALink()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = Populate(Path.Combine(outside, "0"));
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        SymbolicLink.ToDirectory(Root, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>
    /// §5.6, executed, with the server running: it is stopped once, before anything goes, and the run
    /// removes the cached entries and leaves the root and anything unrecognised standing.
    /// </summary>
    [Fact]
    public async Task StopsTheServerOnceAndThenRemovesTheCachedEntries()
    {
        CreateCache();
        var stranger = Populate(Child("lock"));
        var inspector = ServerThatStops();

        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        var stop = Assert.Single(_runner.Invocations);
        Assert.Equal(Sccache, stop.FileName);
        Assert.Equal("--stop-server", stop.Arguments);

        Assert.True(result.Succeeded);
        Assert.Equal(3 * 4096, result.BytesReclaimed);
        Assert.False(Directory.Exists(Child("0")));
        Assert.False(Directory.Exists(Child("preprocessor")));
        Assert.True(Directory.Exists(Root));
        Assert.True(Directory.Exists(stranger));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// sccache answers the stop before its server process has ended, so the clean waits for it to exit
    /// rather than reading the first look as a server that would not stop.
    /// </summary>
    [Fact]
    public async Task WaitsForTheStoppedServerToExitBeforeRemovingAnything()
    {
        CreateCache();
        _environment.WithExecutable("sccache");
        _runner.Responding(Sccache, "--stop-server", "Stopping sccache server...");
        var inspector = new FakeProcessInspector("sccache");
        var clock = new ManualTimeProvider();

        var provider = CreateProvider(inspector, clock);
        var plan = await provider.PlanAsync();

        var running = provider.ExecuteAsync(plan);

        // The server exits only once the clean has looked, found it still running, and waited.
        await clock.WhenWaitingAsync(TimeSpan.FromSeconds(30));
        inspector.WithoutRunning("sccache");
        clock.Advance(TimeSpan.FromSeconds(1));

        var result = await running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(_runner.Invocations);
        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Child("0")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// <c>--stop-server</c> fails when no server is running, so it is not sent then, and the clean is not
    /// reported as having failed a stop it never needed.
    /// </summary>
    [Fact]
    public async Task SendsNoStopWhenNoServerIsRunning()
    {
        CreateCache();
        _environment.WithExecutable("sccache");

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.Empty(_runner.Invocations);
        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Child("a")));
    }

    /// <summary>
    /// The stop is a request. A server that is still running after it holds every removal back, and
    /// what was held back is asserted standing.
    /// </summary>
    [Fact]
    public async Task AServerThatWillNotStopHoldsTheCacheBack()
    {
        CreateCache();
        _environment.WithExecutable("sccache");
        _runner.Responding(Sccache, "--stop-server", string.Empty, exitCode: 2);
        var clock = new ManualTimeProvider();

        var provider = CreateProvider(new FakeProcessInspector("sccache"), clock);
        var plan = await provider.PlanAsync();

        var running = provider.ExecuteAsync(plan);

        // The release waits on the clock between looks for the server to exit, so the clock is moved on
        // each time it does, and the release gives up once the clock passes its limit.
        var patience = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (!running.IsCompleted && DateTime.UtcNow < patience)
        {
            if (clock.Waiting > 0)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            else
            {
                await Task.Delay(10);
            }
        }

        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Single(_runner.Invocations);
        Assert.All(result.Steps, step => Assert.False(step.Succeeded));
        Assert.All(result.Steps, step => Assert.Contains("sccache is running now", step.Message, StringComparison.Ordinal));
        Assert.True(Directory.Exists(Child("0")));
        Assert.True(Directory.Exists(Child("preprocessor")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A server that a build started after the preview, with nothing able to stop it, holds the cache
    /// back rather than having entries removed from under it.
    /// </summary>
    [Fact]
    public async Task AServerStartedAfterThePreviewHoldsTheCacheBackWhenSccacheCannotBeFound()
    {
        CreateCache();
        var inspector = new FakeProcessInspector();

        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        inspector.WithRunning("sccache").PassTheReadingLimit();
        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(_runner.Invocations);
        Assert.True(Directory.Exists(Child("0")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task SaysTheServerWillBeStoppedWhenItIsRunning()
    {
        CreateCache();

        var plan = await CreateProvider(ServerThatStops()).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Information && n.Message.Contains("--stop-server", StringComparison.Ordinal));
        Assert.Empty(_runner.Invocations);
    }

    [Fact]
    public async Task WarnsWhenTheServerIsRunningAndSccacheCannotBeFound()
    {
        CreateCache();

        var plan = await CreateProvider(new FakeProcessInspector("sccache")).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("cannot find sccache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleansInTheDefaultCache()
    {
        CreateCache();

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleansWhereTheVariableMovedTheCache()
    {
        var moved = Path.Combine(_temp.Path, "caches", "sccache");
        Populate(Path.Combine(moved, "1"));
        Populate(Path.Combine(moved, "preprocessor"));
        _environment.WithEnvironmentVariable(SccacheProvider.CacheVariable, moved);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("lock", false)]
    [InlineData("0", true)]
    [InlineData("f", true)]
    [InlineData("preprocessor", true)]
    public void ExploreMayRemoveOnlyWhatSccacheWrites(string relative, bool allowed)
    {
        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(relative.Length == 0 ? Root : Path.Combine(Root, relative)).IsAllowed);
    }
}
