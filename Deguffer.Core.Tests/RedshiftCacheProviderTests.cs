using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Redshift renderer's converted textures. They are reached by name inside Redshift's own folders, so
/// what has to be shown is that only <c>Textures</c> is emptied, that the mesh cache, the preferences, the
/// logs and anything unrecognised beside it survive, that nothing is offered while a Redshift host runs,
/// and that a folder a setting names is followed only where it may be Redshift's.
/// </summary>
public sealed class RedshiftCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes = new();

    public RedshiftCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _volumes.With(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Standalone => Path.Combine(_environment.LocalAppData, "Redshift");

    private string StandaloneTextures => Path.Combine(Standalone, "Cache", "Textures");

    /// <summary>The preferences folder of a Maxon application, laid out as Cinebench 2026 left it.</summary>
    private string Host => Path.Combine(_environment.RoamingAppData, "Maxon", "Maxon Cinema 4D 2026_A1B2C3D4");

    private string HostRedshift => Path.Combine(Host, "Redshift");

    private string HostTextures => Path.Combine(HostRedshift, "Cache", "Textures");

    private RedshiftCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, volumes: _volumes);

    private static string Write(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// One Redshift folder as measured: converted textures in two levels of numbered folders, a mesh cache
    /// with a process's lock, the preferences, a log, and a cache folder nobody has seen yet.
    /// </summary>
    /// <returns>The textures, which go, and everything beside them, which stays.</returns>
    private static (string[] Cached, string[] Kept) CreateRedshiftFolder(string redshift, int bytes = 4096) =>
    (
        [
            Write(Path.Combine(redshift, "Cache", "Textures", "107", "202", "16d9054c043cb86.rstexbin"), bytes),
            Write(Path.Combine(redshift, "Cache", "Textures", "25", "108", "a6d45dd342a0db4.rstexbin"), bytes),
        ],
        [
            Write(Path.Combine(redshift, "Cache", "Meshes", "WORKSTATION", "64868", "lock"), 0),
            Write(Path.Combine(redshift, "Cache", "Shaders", "kernel.bin")),
            Write(Path.Combine(redshift, "preferences.xml"), 544),
            Write(Path.Combine(redshift, "Log", "Log.Latest.0", "log.html")),
        ]);

    [Fact]
    public async Task ReportsNotPresentWhereRedshiftHasNeverRun()
    {
        Write(Path.Combine(Host, "prefs", "c4d.prf"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>Redshift creates <c>Textures</c> each time it starts, so an empty one is no row.</summary>
    [Fact]
    public async Task AnEmptyTexturesFolderIsNotPresence()
    {
        Directory.CreateDirectory(Path.Combine(StandaloneTextures, "107"));
        Write(Path.Combine(HostRedshift, "preferences.xml"));
        Directory.CreateDirectory(HostTextures);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>§3. Redshift converts the textures again by itself, so the row is Tier 1 and ticked.</summary>
    [Fact]
    public async Task PlansTheStandaloneAndEachHostsTexturesAtTier1()
    {
        CreateRedshiftFolder(Standalone, 1024);
        CreateRedshiftFolder(HostRedshift, 2048);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([StandaloneTextures, HostTextures], plan.TargetedPaths);
        Assert.All(plan.Steps, step => Assert.IsType<ClearDirectoryStep>(step));
        Assert.All(plan.Steps, step => Assert.Null(step.LastWritten));
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal((2 * 1024) + (2 * 2048), plan.EstimatedBytes);
        Assert.True(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
    }

    /// <summary>
    /// §5.6, proved by running a plan. The textures go and their folder stays. The mesh cache with a
    /// running program's lock, the preferences, the logs and a cache folder nothing names all survive,
    /// and each named one is asserted rather than merely omitted. So does the rest of the host's
    /// preferences folder.
    /// </summary>
    [Fact]
    public async Task EverythingBesideTheTexturesSurvivesARunAndIsAsserted()
    {
        var (standaloneCached, standaloneKept) = CreateRedshiftFolder(Standalone);
        var (hostCached, hostKept) = CreateRedshiftFolder(HostRedshift);
        var hostPreferences = Write(Path.Combine(Host, "prefs", "c4d.prf"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var redshift in new[] { Standalone, HostRedshift })
        {
            foreach (var path in new[]
            {
                redshift,
                Path.Combine(redshift, "Cache", "Meshes"),
                Path.Combine(redshift, "preferences.xml"),
                Path.Combine(redshift, "Log"),
            })
            {
                Assert.Contains(plan.ProtectedPaths, p =>
                    p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
            }
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.All([.. standaloneCached, .. hostCached], path => Assert.False(File.Exists(path), $"{path} was left."));
        Assert.All(
            [.. standaloneKept, .. hostKept, hostPreferences],
            path => Assert.True(File.Exists(path), $"{path} went with the converted textures."));
        Assert.True(Directory.Exists(StandaloneTextures));
        Assert.True(Directory.Exists(HostTextures));
    }

    /// <summary>
    /// §5.3. Maxon says the cache must stay untouched while a render runs, so while a host runs nothing
    /// is offered, each textures folder is named for why, the row does not read as clear, and Explore
    /// refuses it.
    /// </summary>
    [Theory]
    [InlineData("Cinebench")]
    [InlineData("Cinema 4D")]
    [InlineData("Commandline")]
    [InlineData("maya")]
    [InlineData("houdinifx")]
    [InlineData("hython")]
    [InlineData("3dsmax")]
    [InlineData("blender")]
    [InlineData("redshiftCmdLine")]
    public async Task NothingIsOfferedWhileARedshiftHostRuns(string process)
    {
        var (cached, _) = CreateRedshiftFolder(HostRedshift);

        var provider = CreateProvider(new FakeProcessInspector(process));
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
        Assert.Contains(
            "is running",
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(HostTextures, StringComparison.OrdinalIgnoreCase)).Reason,
            StringComparison.Ordinal);

        var policy = new ExploreActionPolicy(
            [], [], new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.False(policy.MayRemove(HostTextures).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(HostTextures, "107")).IsAllowed);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.All(cached, path => Assert.True(File.Exists(path)));
    }

    /// <summary>The clean asks again, so Cinema 4D opened while the preview was on screen holds the cache back.</summary>
    [Fact]
    public async Task AHostStartedAfterThePreviewHoldsTheCacheBack()
    {
        var (cached, _) = CreateRedshiftFolder(HostRedshift);

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.Equal([HostTextures], plan.TargetedPaths);

        inspector.WithRunning("Cinema 4D");
        var result = await provider.ExecuteAsync(plan);

        Assert.All(cached, path => Assert.True(File.Exists(path), $"{path} was removed under a Cinema 4D started after the preview."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7.1. Explore may take from a cache only <c>Textures</c>, never the mesh cache or a folder nobody
    /// has classified, and nothing at all from Redshift's own folder.
    /// </summary>
    [Theory]
    [InlineData("Textures", true)]
    [InlineData("Meshes", false)]
    [InlineData("Shaders", false)]
    [InlineData("Unrecognised", false)]
    public async Task ExploreMayTakeOnlyTheTexturesFromACache(string child, bool recognised)
    {
        CreateRedshiftFolder(HostRedshift);

        var roots = await CreateProvider().DiscoverToolRootsAsync();
        var cache = Assert.Single(roots, r => r.Path.Equals(Path.Combine(HostRedshift, "Cache"), StringComparison.OrdinalIgnoreCase));
        var redshift = Assert.Single(roots, r => r.Path.Equals(HostRedshift, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(recognised, cache.RecognisesFolder(child));
        Assert.False(redshift.RecognisesFolder("Cache"));
        Assert.False(redshift.RecognisesFolder("Log"));
    }

    /// <summary>
    /// <c>REDSHIFT_CACHEPATH</c> moves the cache, and Redshift keeps <c>Textures</c> inside it. Only those
    /// are emptied there, and the mesh cache and anything else in the folder survive (§5.6).
    /// </summary>
    [Fact]
    public async Task FollowsTheFolderTheVariableNames()
    {
        var moved = Path.Combine(_temp.Path, "RenderCache");
        var cached = Write(Path.Combine(moved, "Textures", "1", "109", "683a659da5e1f846.rstexbin"));
        var lockFile = Write(Path.Combine(moved, "Meshes", "WORKSTATION", "4242", "lock"), 0);
        var unnamed = Write(Path.Combine(moved, "Shaders", "kernel.bin"));
        _environment.WithEnvironmentVariable(RedshiftCacheLayout.CacheVariable, moved);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(moved, "Textures")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(moved, "Meshes"), StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(File.Exists(cached));
        Assert.True(File.Exists(lockFile));
        Assert.True(File.Exists(unnamed));
    }

    /// <summary>The variable naming the default folder names one cache, offered once.</summary>
    [Fact]
    public async Task TheVariableNamingTheDefaultCacheDoesNotDeclareItTwice()
    {
        CreateRedshiftFolder(Standalone);
        _environment.WithEnvironmentVariable(RedshiftCacheLayout.CacheVariable, Path.Combine(Standalone, "Cache") + @"\");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([StandaloneTextures], plan.TargetedPaths);
    }

    /// <summary>
    /// A variable naming a folder inside another cache's textures adds nothing: emptying the outer
    /// textures takes it, and declaring it too would assert that its mesh cache survived a run bound to
    /// remove it (§5.6).
    /// </summary>
    [Fact]
    public async Task AVariableInsideAnotherCachesTexturesIsNotDeclared()
    {
        CreateRedshiftFolder(Standalone);
        var nested = Path.Combine(StandaloneTextures, "nested");
        Write(Path.Combine(nested, "Textures", "1", "2", "inner.rstexbin"));
        Write(Path.Combine(nested, "Meshes", "lock"), 0);
        _environment.WithEnvironmentVariable(RedshiftCacheLayout.CacheVariable, nested);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([StandaloneTextures], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(nested));
    }

    /// <summary>
    /// A variable naming the user's own profile is declined and said out loud, and the folder named
    /// <c>Textures</c> in it, which may be the user's own, is never reached.
    /// </summary>
    [Fact]
    public async Task DeclinesAVariableNamingTheProfileAndReachesNothingInIt()
    {
        var mine = Write(Path.Combine(_environment.UserProfile, "Textures", "brick_albedo.rstexbin"));
        _environment.WithEnvironmentVariable(RedshiftCacheLayout.CacheVariable, _environment.UserProfile);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(RedshiftCacheLayout.CacheVariable, StringComparison.Ordinal));
        Assert.True(File.Exists(mine));
        Assert.DoesNotContain(
            await provider.DiscoverToolRootsAsync(),
            r => LongPath.Contains(_environment.UserProfile, r.Path) && r.RecognisesFolder("Textures"));
    }

    /// <summary>A relative value could name any folder, so it is said out loud and resolved against nothing.</summary>
    [Fact]
    public async Task SaysSoWhenTheVariableIsNotAFullPath()
    {
        _environment.WithEnvironmentVariable(RedshiftCacheLayout.CacheVariable, @"Redshift\Cache");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(@"'Redshift\Cache'", StringComparison.Ordinal));
    }

    /// <summary>
    /// The <c>CacheFolder</c> preference was measured naming a folder Redshift did not use, so it moves
    /// nothing: a folder it names is never reached.
    /// </summary>
    [Fact]
    public async Task TheCacheFolderPreferenceIsNotFollowed()
    {
        CreateRedshiftFolder(HostRedshift);
        var named = Path.Combine(_temp.Path, "Projects");
        var mine = Write(Path.Combine(named, "Textures", "1", "2", "converted_by_hand.rstexbin"));
        File.WriteAllText(
            Path.Combine(HostRedshift, "preferences.xml"),
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <redshift_preferences version="1.2">
            	<preference name="CacheFolder" type="string" value="{named}" />
            </redshift_preferences>
            """);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([HostTextures], plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(mine));
    }

    /// <summary>
    /// A cache on another computer's disk may be in use by a render there, which no process check here
    /// can see, so it is withheld and named. A machine without Redshift on such a profile is told nothing.
    /// </summary>
    [Fact]
    public async Task LeavesACacheOnAShareAloneAndSaysNothingWhereThereIsNone()
    {
        _volumes.Without(_temp.Path);

        Assert.False(await CreateProvider().IsPresentAsync());

        var (cached, _) = CreateRedshiftFolder(HostRedshift);
        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(Path.Combine(HostRedshift, "Cache"), StringComparison.OrdinalIgnoreCase));
        Assert.All(cached, path => Assert.True(File.Exists(path)));
    }

    /// <summary>A textures folder that is a link is declined and named, and nothing it points at is touched.</summary>
    [Fact]
    public async Task ATexturesFolderThatIsALinkIsLeftAlone()
    {
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        var target = Write(Path.Combine(elsewhere, "1", "2", "a.rstexbin"));
        Directory.CreateDirectory(Path.Combine(HostRedshift, "Cache"));
        SymbolicLink.ToDirectory(HostTextures, elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(HostTextures, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(target));
    }
}
