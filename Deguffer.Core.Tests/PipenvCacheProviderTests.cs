using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// pipenv's cache holds pip's layout and pipenv's own resolver cache side by side, so these are about
/// which route clears which folder, and about what neither route may take (§5.2, §5.6).
///
/// <para>Everything runs against a synthetic profile through <see cref="FakeUserEnvironment"/>, and pip's
/// purge is a fake that empties the folders the real one was observed to empty. No pipenv was installed
/// on the machine these were written on.</para>
/// </summary>
public sealed class PipenvCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeProcessRunner _runner = new();

    public PipenvCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private PipenvCacheProvider CreateProvider() =>
        new(_environment, _runner, FakeProcessInspector.NothingRunning, system: _system);

    private string Root => Path.Combine(_environment.LocalAppData, "pipenv", "pipenv", "Cache");

    private string Child(string name) => Path.Combine(Root, name);

    /// <summary>The pip the fake environment resolves, so a test names the program a step must run.</summary>
    private string Pip => _environment.FindExecutable("pip")!;

    /// <summary>A directory holding one file, so it measures above zero and is selectable.</summary>
    private static string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    /// <summary>pip's three folders and pipenv's resolver cache, each holding a file.</summary>
    private void CreateCache(string? root = null)
    {
        foreach (var name in (string[])["http", "http-v2", "wheels", "pipenv-manifests"])
        {
            Populate(Path.Combine(root ?? Root, name));
        }
    }

    /// <summary>pip installed, with a purge that empties pip's layout in the folder it is given and nothing else.</summary>
    private void PipThatPurges()
    {
        _environment.WithExecutable("pip");

        _runner.Replying(Pip, arguments =>
        {
            foreach (var name in (string[])["http", "http-v2", "wheels"])
            {
                var folder = Path.Combine(Root, name);

                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }

            return new CommandOutcome(0, "Files removed: 3", string.Empty);
        });
    }

    [Fact]
    public async Task ReportsNotPresentWhenPipenvHasNeverCached()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// platformdirs names the application twice on Windows, so the cache is in <c>pipenv\pipenv\Cache</c>,
    /// not the <c>pipenv\Cache</c> pipenv's documentation names.
    /// </summary>
    [Fact]
    public async Task FindsTheCacheWherePipenvWritesIt()
    {
        Populate(Path.Combine(_environment.LocalAppData, "pipenv", "Cache", "http-v2"));

        Assert.False(await CreateProvider().IsPresentAsync());

        Populate(Child("http-v2"));

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PurgesPipsLayoutWithPipsOwnCommandAndRemovesTheResolverCacheByPath()
    {
        CreateCache();
        _environment.WithExecutable("pip");

        var plan = await CreateProvider().PlanAsync();

        var purge = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(Pip, purge.FileName);
        Assert.Equal($"--cache-dir \"{Root}\" cache purge", purge.Arguments);
        Assert.Equal(
            [Child("http"), Child("http-v2"), Child("wheels")],
            purge.MeasuredPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(3 * 4096, purge.EstimatedBytes);

        Assert.Equal([Child("pipenv-manifests")], plan.TargetedPaths);
        Assert.DoesNotContain(Root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Root, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.Empty(_runner.Invocations);
    }

    /// <summary>pip is installed as pip3 on plenty of machines, and that is still pip.</summary>
    [Fact]
    public async Task FindsPipUnderItsPip3Spelling()
    {
        CreateCache();
        _environment.WithExecutable("pip3");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(_environment.FindExecutable("pip3"), Assert.Single(plan.Steps.OfType<RunCommandStep>()).FileName);
    }

    /// <summary>Without pip there is no command, and what is in pip's folders is the same cache either way.</summary>
    [Fact]
    public async Task RemovesPipsLayoutByPathWhenPipCannotBeFound()
    {
        CreateCache();

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(
            [Child("http"), Child("http-v2"), Child("pipenv-manifests"), Child("wheels")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("cannot find pip", StringComparison.Ordinal));
    }

    /// <summary>
    /// pip's purge would empty a folder of its layout through a link, so a pip folder that is a link
    /// sends the plan down the path route, where the link is declined and the rest still goes.
    /// </summary>
    [Fact]
    public async Task APipFolderThatIsALinkIsDeclinedAndNotPurgedThrough()
    {
        _environment.WithExecutable("pip");
        Populate(Child("http"));
        Populate(Child("pipenv-manifests"));
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));
        SymbolicLink.ToDirectory(Child("wheels"), outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(
            [Child("http"), Child("pipenv-manifests")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Child("wheels"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>§5.2: a name pipenv's cache does not recognise is Tier 4, left alone and asserted standing.</summary>
    [Fact]
    public async Task AnUnrecognisedChildIsLeftAloneAndAssertedStanding()
    {
        CreateCache();
        var downloads = Populate(Child("pkgs"));

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(downloads, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(downloads, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("'pkgs'", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6, executed: pip's purge and the path removal both run, and the cache folder, pipenv's folder
    /// above it and the child nobody recognises are all still standing.
    /// </summary>
    [Fact]
    public async Task AClean_ClearsBothRoutesAndLeavesTheRootAndTheUnrecognisedStanding()
    {
        CreateCache();
        var downloads = Populate(Child("pkgs"));
        PipThatPurges();

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var purge = Assert.Single(_runner.Invocations);
        Assert.Equal(Pip, purge.FileName);
        Assert.Equal($"--cache-dir \"{Root}\" cache purge", purge.Arguments);

        Assert.True(result.Succeeded);
        Assert.Equal(4 * 4096, result.BytesReclaimed);
        Assert.False(Directory.Exists(Child("wheels")));
        Assert.False(Directory.Exists(Child("pipenv-manifests")));
        Assert.True(Directory.Exists(Root));
        Assert.True(Directory.Exists(Path.Combine(_environment.LocalAppData, "pipenv")));
        Assert.True(File.Exists(Path.Combine(downloads, "payload.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task HonoursTheCacheVariableWhenItNamesAFolderOfItsOwn()
    {
        var moved = Path.Combine(_temp.Path, "caches", "pipenv");
        CreateCache(moved);
        _environment.WithExecutable("pip");
        _environment.WithEnvironmentVariable(PipenvCacheProvider.CacheVariable, moved);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal($"--cache-dir \"{moved}\" cache purge", Assert.Single(plan.Steps.OfType<RunCommandStep>()).Arguments);
        Assert.Equal([Path.Combine(moved, "pipenv-manifests")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>http</c> and <c>wheels</c> are names anything may use, so a variable naming the profile offers nothing.</summary>
    [Fact]
    public async Task WillNotTreatAFolderHoldingTheProfileAsPipenvs()
    {
        var shared = Populate(Path.Combine(_environment.UserProfile, "wheels"));
        _environment.WithExecutable("pip");
        _environment.WithEnvironmentVariable(PipenvCacheProvider.CacheVariable, _environment.UserProfile);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.DoesNotContain(provider.ToolRoots, root => root.Path.Equals(_environment.UserProfile, StringComparison.OrdinalIgnoreCase));

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(shared, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("will not treat that as pipenv's cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeclinesARootThatIsItselfALink()
    {
        _environment.WithExecutable("pip");
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = Populate(Path.Combine(outside, "http-v2"));
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        SymbolicLink.ToDirectory(Root, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(Directory.Exists(stranger));
        Assert.True(plan.WasNotExamined);
    }

    [Fact]
    public void EveryDeclaredChildIsARegenerableCache()
    {
        Assert.Equal(
            ["http", "http-v2", "pipenv-manifests", "wheels"],
            PipenvCacheProvider.Children.DisposableNames.Order(StringComparer.Ordinal));
        Assert.All(
            PipenvCacheProvider.Children.DisposableNames,
            name => Assert.Equal(SafetyTier.RegenerableCache, PipenvCacheProvider.Children.Classify(name).Tier));
        Assert.False(PipenvCacheProvider.Children.IsDisposable("pkgs"));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleansByEitherRoute()
    {
        CreateCache();

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));

        _environment.WithExecutable("pip");

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("pkgs", false)]
    [InlineData("http", true)]
    [InlineData("http-v2", true)]
    [InlineData("wheels", true)]
    [InlineData("pipenv-manifests", true)]
    public void ExploreMayRemoveOnlyWhatPipenvCaches(string relative, bool allowed)
    {
        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(relative.Length == 0 ? Root : Path.Combine(Root, relative)).IsAllowed);
    }
}
