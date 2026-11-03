using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Redshift renderer's converted textures. They are reached by name inside Redshift's own folders, so
/// what has to be shown is that only the numbered folders in <c>Textures</c> go, that anything else in
/// <c>Textures</c>, the mesh cache, the preferences, the logs and anything unrecognised beside them
/// survive, that nothing is offered while a Redshift host runs, and that a folder a setting names is
/// followed only where it is Redshift's.
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

    /// <summary>A Maxon application's preferences folder, invented, holding the layout Cinebench 2026 left.</summary>
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
    /// One Redshift folder as observed: converted textures in two levels of numbered folders, a mesh
    /// cache with a process's lock, the preferences and a log. Beside them, what Redshift was not seen
    /// writing: a cache folder nobody has classified, and in <c>Textures</c> an image, a folder of the
    /// user's, and a folder whose number is not written the way Redshift writes it.
    /// </summary>
    /// <returns>The converted textures, which go, and everything else, which stays.</returns>
    private static (string[] Cached, string[] Kept) CreateRedshiftFolder(string redshift, int bytes = 4096) =>
    (
        [
            Write(Path.Combine(redshift, "Cache", "Textures", "107", "202", "16d9054c043cb86.rstexbin"), bytes),
            Write(Path.Combine(redshift, "Cache", "Textures", "25", "108", "a6d45dd342a0db4.rstexbin"), bytes),
        ],
        [
            Write(Path.Combine(redshift, "Cache", "Textures", "brick_albedo.jpg")),
            Write(Path.Combine(redshift, "Cache", "Textures", "Wood", "oak_converted.rstexbin")),
            Write(Path.Combine(redshift, "Cache", "Textures", "007", "1", "kept.rstexbin")),
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

    /// <summary>A <c>Textures</c> folder with no numbered folder in it holds nothing this row may take.</summary>
    [Fact]
    public async Task ATexturesFolderWithNoNumberedFolderIsNotPresence()
    {
        Directory.CreateDirectory(StandaloneTextures);
        Write(Path.Combine(HostRedshift, "preferences.xml"));
        Write(Path.Combine(HostTextures, "brick_albedo.jpg"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// §3. Redshift converts the textures again by itself, and the layout was observed, so the row is
    /// Tier 1 and ticked. Each numbered folder is a part, listed under the cache it is in.
    /// </summary>
    [Fact]
    public async Task PlansTheNumberedFoldersOfTheStandaloneAndEachHostsCacheAtTier1()
    {
        CreateRedshiftFolder(Standalone, 1024);
        CreateRedshiftFolder(HostRedshift, 2048);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[]
            {
                Path.Combine(StandaloneTextures, "107"), Path.Combine(StandaloneTextures, "25"),
                Path.Combine(HostTextures, "107"), Path.Combine(HostTextures, "25"),
            }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan.Steps, step => Assert.IsType<DeleteDirectoryStep>(step));
        Assert.Equal(
            [Path.Combine(Standalone, "Cache"), Path.Combine(HostRedshift, "Cache")],
            plan.Steps.Select(step => step.Group).Distinct().Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal(ProviderBasis.Observed, provider.Basis);
        Assert.Equal((2 * 1024) + (2 * 2048), plan.EstimatedBytes);
        Assert.True(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
    }

    /// <summary>
    /// §5.6, proved by running a plan. The numbered folders go and <c>Textures</c> stays. The image, the
    /// user's folder and the oddly numbered folder in <c>Textures</c>, the mesh cache with a running
    /// program's lock, the preferences and the logs all survive, and each is asserted rather than merely
    /// omitted. A cache folder nothing names and the rest of the host's preferences folder are never
    /// listed, so they are checked on the disk only.
    /// </summary>
    [Fact]
    public async Task EverythingButTheNumberedFoldersSurvivesARunAndIsAsserted()
    {
        var (standaloneCached, standaloneKept) = CreateRedshiftFolder(Standalone);
        var (hostCached, hostKept) = CreateRedshiftFolder(HostRedshift);
        var hostPreferences = Write(Path.Combine(Host, "prefs", "c4d.prf"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var redshift in new[] { Standalone, HostRedshift })
        {
            var textures = Path.Combine(redshift, "Cache", "Textures");

            foreach (var path in new[]
            {
                redshift,
                textures,
                Path.Combine(textures, "Wood"),
                Path.Combine(textures, "007"),
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
    [InlineData("Team Render Client")]
    [InlineData("maya")]
    [InlineData("houdinifx")]
    [InlineData("hython")]
    [InlineData("3dsmax")]
    [InlineData("3dsmaxcmd")]
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

    /// <summary>The program measured holding the cache is the first one the user is told to close.</summary>
    [Fact]
    public async Task TheHeldReasonNamesCinebench()
    {
        CreateRedshiftFolder(HostRedshift);

        var plan = await CreateProvider(new FakeProcessInspector("Cinebench")).PlanAsync();

        Assert.Contains(
            "Close Cinebench",
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(HostTextures, StringComparison.OrdinalIgnoreCase)).Reason,
            StringComparison.Ordinal);
    }

    /// <summary>The clean asks again, so Cinema 4D opened while the preview was on screen holds the cache back.</summary>
    [Fact]
    public async Task AHostStartedAfterThePreviewHoldsTheCacheBack()
    {
        var (cached, _) = CreateRedshiftFolder(HostRedshift);

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.Steps);

        inspector.WithRunning("Cinema 4D");
        var result = await provider.ExecuteAsync(plan);

        Assert.All(cached, path => Assert.True(File.Exists(path), $"{path} was removed under a Cinema 4D started after the preview."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7.1. Explore may take from <c>Textures</c> only the numbered folders, written as Redshift writes
    /// them, and nothing at all from the cache or from Redshift's own folder.
    /// </summary>
    [Theory]
    [InlineData("0", true)]
    [InlineData("107", true)]
    [InlineData("255", true)]
    [InlineData("256", false)]
    [InlineData("007", false)]
    [InlineData("-1", false)]
    [InlineData("Wood", false)]
    public async Task ExploreMayTakeOnlyTheNumberedFoldersFromTextures(string child, bool recognised)
    {
        CreateRedshiftFolder(HostRedshift);

        var roots = await CreateProvider().DiscoverToolRootsAsync();
        var textures = Assert.Single(roots, r => r.Path.Equals(HostTextures, StringComparison.OrdinalIgnoreCase));
        var cache = Assert.Single(roots, r => r.Path.Equals(Path.Combine(HostRedshift, "Cache"), StringComparison.OrdinalIgnoreCase));
        var redshift = Assert.Single(roots, r => r.Path.Equals(HostRedshift, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(recognised, textures.RecognisesFolder(child));
        Assert.False(cache.RecognisesFolder("Textures"));
        Assert.False(cache.RecognisesFolder("Meshes"));
        Assert.False(redshift.RecognisesFolder("Cache"));
        Assert.False(redshift.RecognisesFolder("Log"));
    }

    /// <summary>
    /// <c>REDSHIFT_CACHEPATH</c> moves the cache, and Redshift keeps <c>Textures</c> and <c>Meshes</c> in
    /// it. Only the numbered folders go there, and the user's images in <c>Textures</c>, the mesh cache
    /// and anything else in the folder survive (§5.6).
    /// </summary>
    [Fact]
    public async Task FollowsTheFolderTheVariableNames()
    {
        var moved = Path.Combine(_temp.Path, "RenderCache");
        var cached = Write(Path.Combine(moved, "Textures", "1", "109", "683a659da5e1f846.rstexbin"));
        var image = Write(Path.Combine(moved, "Textures", "brick_albedo.jpg"));
        var lockFile = Write(Path.Combine(moved, "Meshes", "WORKSTATION", "4242", "lock"), 0);
        var unnamed = Write(Path.Combine(moved, "Shaders", "kernel.bin"));
        _environment.WithEnvironmentVariable(RedshiftCacheLayout.CacheVariable, moved);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(moved, "Textures", "1")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(moved, "Meshes"), StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(File.Exists(cached));
        Assert.All([image, lockFile, unnamed], path => Assert.True(File.Exists(path), $"{path} went."));
    }

    /// <summary>
    /// A variable naming a project folder whose <c>Textures</c> holds the user's images, numbered folders
    /// included, and no mesh cache: nothing shows the folder is Redshift's, so it is named and left alone,
    /// and Explore is given nothing in it.
    /// </summary>
    [Fact]
    public async Task LeavesAVariableFolderWithNoMeshCacheAlone()
    {
        var project = Path.Combine(_temp.Path, "Projects", "Job1");
        var mine = new[]
        {
            Write(Path.Combine(project, "Textures", "1", "brick_albedo.exr")),
            Write(Path.Combine(project, "Textures", "brick_albedo.rstexbin")),
        };
        _environment.WithEnvironmentVariable(RedshiftCacheLayout.CacheVariable, project);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(project, StringComparison.OrdinalIgnoreCase)
            && n.Message.Contains("Meshes", StringComparison.Ordinal));
        Assert.DoesNotContain(await provider.DiscoverToolRootsAsync(), r => LongPath.Contains(project, r.Path));

        await provider.ExecuteAsync(plan);

        Assert.All(mine, path => Assert.True(File.Exists(path)));
    }

    /// <summary>The variable naming the default folder names one cache, offered once.</summary>
    [Fact]
    public async Task TheVariableNamingTheDefaultCacheDoesNotDeclareItTwice()
    {
        CreateRedshiftFolder(Standalone);
        _environment.WithEnvironmentVariable(RedshiftCacheLayout.CacheVariable, Path.Combine(Standalone, "Cache") + @"\");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            [Path.Combine(StandaloneTextures, "107"), Path.Combine(StandaloneTextures, "25")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The variable set to the default folder names Redshift's own cache, which needs no mesh cache to
    /// prove it, so a default cache Redshift has not yet made <c>Meshes</c> in is still offered, and the
    /// plan does not say it was left alone.
    /// </summary>
    [Fact]
    public async Task TheVariableNamingTheDefaultCacheNeedsNoMeshCache()
    {
        Write(Path.Combine(StandaloneTextures, "107", "202", "16d9054c043cb86.rstexbin"));
        _environment.WithEnvironmentVariable(RedshiftCacheLayout.CacheVariable, Path.Combine(Standalone, "Cache"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Path.Combine(StandaloneTextures, "107")], plan.TargetedPaths);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains(RedshiftCacheLayout.CacheVariable, StringComparison.Ordinal));
    }

    /// <summary>
    /// A variable naming a folder inside another cache's textures adds nothing: that cache's walk may
    /// remove it, and declaring it too would assert that its mesh cache survived a run bound to remove it
    /// (§5.6).
    /// </summary>
    [Fact]
    public async Task AVariableInsideAnotherCachesTexturesIsNotDeclared()
    {
        CreateRedshiftFolder(Standalone);
        var nested = Path.Combine(StandaloneTextures, "12", "nested");
        Write(Path.Combine(nested, "Textures", "1", "2", "inner.rstexbin"));
        Write(Path.Combine(nested, "Meshes", "lock"), 0);
        _environment.WithEnvironmentVariable(RedshiftCacheLayout.CacheVariable, nested);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(plan.TargetedPaths, path => LongPath.Contains(nested, path));
        Assert.DoesNotContain(plan.ProtectedPaths, p => LongPath.Contains(nested, p.Path));

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
        var mine = Write(Path.Combine(_environment.UserProfile, "Textures", "1", "brick_albedo.rstexbin"));
        Directory.CreateDirectory(Path.Combine(_environment.UserProfile, "Meshes"));
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
            r => LongPath.Contains(_environment.UserProfile, r.Path) && r.RecognisesFolder("1"));
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
    /// The <c>CacheFolder</c> preference was observed naming a folder Redshift did not use, so it moves
    /// nothing: a folder it names is never reached, even one laid out as a cache.
    /// </summary>
    [Fact]
    public async Task TheCacheFolderPreferenceIsNotFollowed()
    {
        CreateRedshiftFolder(HostRedshift);
        var named = Path.Combine(_temp.Path, "Projects");
        var mine = Write(Path.Combine(named, "Textures", "1", "2", "converted_by_hand.rstexbin"));
        Directory.CreateDirectory(Path.Combine(named, "Meshes"));
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

        Assert.All(plan.TargetedPaths, path => Assert.True(LongPath.Contains(HostTextures, path), path));

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

    /// <summary>A numbered folder that is a link is declined and named, while its real siblings go.</summary>
    [Fact]
    public async Task ANumberedFolderThatIsALinkIsLeftAlone()
    {
        var (cached, _) = CreateRedshiftFolder(HostRedshift);
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        var target = Write(Path.Combine(elsewhere, "2", "a.rstexbin"));
        var link = Path.Combine(HostTextures, "9");
        SymbolicLink.ToDirectory(link, elsewhere);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(plan.TargetedPaths, path => path.Equals(link, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.All(cached, path => Assert.False(File.Exists(path)));
        Assert.True(File.Exists(target));
    }
}
