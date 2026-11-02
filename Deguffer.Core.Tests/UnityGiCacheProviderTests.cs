using System.Text;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Unity Editor's shared GI cache. It is reached by name inside Unity's own folder, so what has to
/// be shown is that the build cache and Unity's other data beside it survive, that nothing is offered
/// while an Editor runs, and that a cache Unity's settings moved is named and never reached.
/// </summary>
public sealed class UnityGiCacheProviderTests : IDisposable
{
    private const string FolderValue = UnityGiCacheLayout.CustomFolderPreference + "_h1234567890";
    private const string LocationValue = UnityGiCacheLayout.CustomLocationPreference + "_h987654321";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public UnityGiCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Unity => Path.Combine(_environment.LocalAppData, "Unity");

    private string Caches => Path.Combine(Unity, "Caches");

    private string Cache => Path.Combine(Caches, "GiCache");

    private string BuildCache => Path.Combine(Caches, "bee");

    private string CurlDatabase => Path.Combine(Caches, "CurlRequestCache.db");

    private string Licences => Path.Combine(Unity, "licenses");

    private UnityGiCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    private static string Populate(string folder, string file, int bytes = 4096)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, file), new byte[bytes]);
        return folder;
    }

    /// <summary>The folder layout of a machine where the Editor has baked lighting and built a player.</summary>
    private void CreateLayout()
    {
        Populate(Path.Combine(Cache, "3f"), "3f9a0c1e2b4d5f60.lz4", 8192);
        Populate(Path.Combine(BuildCache, "001deb7824db37e8"), "artifact", 2048);
        Directory.CreateDirectory(Caches);
        File.WriteAllBytes(CurlDatabase, new byte[512]);
        Populate(Path.Combine(Unity, "cache", "upm"), "packages.json");
        Populate(Path.Combine(Unity, "Editor"), "Editor.log");
        Populate(Licences, "UnityEntitlementLicense.xml");
    }

    private void MoveCacheTo(string folder)
    {
        _environment.WithRegistryNumber(UnityEditorPreferences.Key, LocationValue, 1);
        _environment.WithRegistryBytes(UnityEditorPreferences.Key, FolderValue, Encoding.UTF8.GetBytes(folder.Replace('\\', '/') + "\0"));
    }

    [Fact]
    public async Task ReportsNotPresentWhereUnityHasNeverRun()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// Unity's folder holds the build cache and the licence too, and the Editor creates an empty
    /// <c>GiCache</c> each time it starts, so neither is a cache to report.
    /// </summary>
    [Fact]
    public async Task UnitysFolderAndAnEmptyCacheFolderAreNotPresence()
    {
        Populate(BuildCache, "artifact");
        Populate(Licences, "UnityEntitlementLicense.xml");
        Directory.CreateDirectory(Path.Combine(Cache, "00"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>§3. The refill is a lighting bake, so the row is Tier 2 and is not ticked for the user.</summary>
    [Fact]
    public async Task PlansTheCacheAloneAtTier2AndDoesNotPreSelectIt()
    {
        CreateLayout();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(Cache, Assert.Single(plan.TargetedPaths));
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(8192, plan.EstimatedBytes);
        Assert.Null(Assert.Single(plan.Steps).LastWritten);
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
    }

    /// <summary>
    /// §5.6 against the neighbours, proved by running a plan. The build cache is the consequential one,
    /// and the database is a file. A neighbour nothing names is here too: it is unreachable by
    /// construction, which only a run shows.
    /// </summary>
    [Fact]
    public async Task UnitysOtherDataSurvivesARunAndIsAssertedRatherThanMerelyOmitted()
    {
        CreateLayout();
        var unnamed = Populate(Path.Combine(Caches, "SomethingNew"), "state.bin");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in new[] { Unity, Caches, BuildCache, CurlDatabase, Path.Combine(Unity, "cache"), Path.Combine(Unity, "Editor"), Licences })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Cache));
        Assert.All(
            [Unity, Caches, BuildCache, Licences, Path.Combine(Unity, "cache", "upm"), Path.Combine(Unity, "Editor"), unnamed],
            path => Assert.True(Directory.Exists(path), $"{path} went with the GI cache."));
        Assert.True(File.Exists(CurlDatabase), "Unity's request database went with the GI cache.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.6 has to fail loudly, or it is decoration.</summary>
    [Fact]
    public async Task VerificationFailsLoudlyIfTheBuildCacheVanished()
    {
        CreateLayout();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(BuildCache, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(BuildCache, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A cache reached by name has none of the protection an enumeration gives, so a link is named and not followed.</summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task ALinkedCacheIsNamedRatherThanFollowed(DirectoryLinkKind kind)
    {
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"), "irreplaceable.bin");

        Directory.CreateDirectory(Caches);
        DirectoryLink.Create(kind, Cache, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(outside, "irreplaceable.bin")), "a linked GI cache was deleted through.");
    }

    /// <summary>
    /// §5.3: Unity says deleting the cache under a running Editor is not safe, so nothing is offered, the
    /// cache is named, the row does not read as clear, and Explore refuses it.
    /// </summary>
    [Fact]
    public async Task NothingIsOfferedWhileAnEditorRuns()
    {
        CreateLayout();

        var provider = CreateProvider(new FakeProcessInspector("Unity"));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("Unity Editor is running", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Cache, StringComparison.OrdinalIgnoreCase));

        var held = Assert.Single(await provider.DiscoverToolRootsAsync());
        Assert.Equal(Cache, held.Path);
        Assert.False(new ExploreActionPolicy([], [.. provider.ToolRoots, held], new FakeVolumeInventory()).MayRemove(Cache).IsAllowed);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(Cache));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>The clean asks again, so an Editor opened while the preview was on screen holds the cache back.</summary>
    [Fact]
    public async Task AnEditorStartedAfterThePreviewHoldsTheCacheBack()
    {
        CreateLayout();

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.Equal([Cache], plan.TargetedPaths);

        inspector.WithRunning("Unity");
        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(Cache), "the GI cache was removed under an Editor started after the preview");
        Assert.Equal("Nothing was removed: Unity is running now.", Assert.Single(result.Steps).Message);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>Unity Hub is not the Editor and holds no reference into the cache.</summary>
    [Fact]
    public async Task UnityHubRunningDoesNotHoldTheCacheBack()
    {
        CreateLayout();

        var plan = await CreateProvider(new FakeProcessInspector("Unity Hub")).PlanAsync();

        Assert.Equal([Cache], plan.TargetedPaths);
    }

    /// <summary>
    /// A cache Unity's settings move is in a folder the user picked, where nothing tells Unity's files
    /// from theirs. It is named, never reached, and the default cache it left behind is still offered.
    /// </summary>
    [Fact]
    public async Task AMovedCacheIsNamedAndNeverReached()
    {
        CreateLayout();
        var moved = Populate(Path.Combine(_temp.Path, "D", "Lighting"), "3f9a0c1e2b4d5f60.lz4");
        var mine = Populate(Path.Combine(moved, "Notes"), "my-notes.txt");
        MoveCacheTo(moved);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([Cache], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains(moved, StringComparison.OrdinalIgnoreCase) && n.Message.Contains("Clean Cache", StringComparison.Ordinal));
        Assert.DoesNotContain(await provider.CleanedPlacesAsync(), place => place.Path.StartsWith(moved, StringComparison.OrdinalIgnoreCase));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(Path.Combine(moved, "3f9a0c1e2b4d5f60.lz4")), "a moved GI cache was reached.");
        Assert.True(Directory.Exists(mine));
    }

    /// <summary>With nothing at the default, the row still appears to say where the cache went, and does not read as clear.</summary>
    [Fact]
    public async Task AMovedCacheWithNothingAtTheDefaultIsARowThatIsNotClear()
    {
        var moved = Populate(Path.Combine(_temp.Path, "D", "Lighting"), "3f9a0c1e2b4d5f60.lz4");
        MoveCacheTo(moved);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(moved, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A custom location turned on whose folder cannot be read is still a move, and the plan says so.</summary>
    [Fact]
    public async Task ACustomLocationWithNoFolderReadIsStillNamedAsAMove()
    {
        CreateLayout();
        _environment.WithRegistryNumber(UnityEditorPreferences.Key, LocationValue, 1);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Cache], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not read which folder", StringComparison.Ordinal));
    }

    /// <summary>
    /// A folder that is not fully qualified names nowhere Deguffer can say Unity resolves it against, so
    /// the plan says it could not read which folder, rather than naming a path relative to Deguffer.
    /// </summary>
    [Fact]
    public async Task ARelativeCustomFolderIsAMoveToAFolderNotNamed()
    {
        CreateLayout();
        MoveCacheTo("Lighting");

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains("could not read which folder", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("'Lighting'", StringComparison.Ordinal));
    }

    /// <summary>The default folder spelled with a <c>..</c> segment is still the default, and moves nothing.</summary>
    [Fact]
    public async Task TheDefaultSpelledAnotherWayIsNoMove()
    {
        CreateLayout();
        MoveCacheTo(Path.Combine(Caches, "bee", "..", "GiCache") + @"\");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Cache], plan.TargetedPaths);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("GI Cache preferences", StringComparison.Ordinal));
    }

    /// <summary>
    /// Unity keeps the folder after the custom location is turned off, and a custom location that names
    /// the default folder moves nothing. Neither is a move to report.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoMoveIsReportedWhereTheCacheIsStillAtTheDefault(bool namesTheDefault)
    {
        CreateLayout();
        MoveCacheTo(namesTheDefault ? Cache : Path.Combine(_temp.Path, "D", "Lighting"));

        if (!namesTheDefault)
        {
            _environment.WithRegistryNumber(UnityEditorPreferences.Key, LocationValue, 0);
        }

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Cache], plan.TargetedPaths);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("GI Cache preferences", StringComparison.Ordinal));
    }

    /// <summary>The hold ages with the process table: with every Editor closed, Explore may remove the cache again.</summary>
    [Fact]
    public async Task ExploreAllowsTheCacheOnceEveryEditorIsClosed()
    {
        CreateLayout();
        MoveCacheTo(Path.Combine(_temp.Path, "D", "Lighting"));

        var provider = CreateProvider();
        var discovered = await provider.DiscoverToolRootsAsync();

        Assert.Empty(discovered);
        Assert.True(new ExploreActionPolicy([], [.. provider.ToolRoots, .. discovered], new FakeVolumeInventory()).MayRemove(Cache).IsAllowed);
    }

    /// <summary>While an Editor runs, Explore refuses the moved folder as well as the default.</summary>
    [Fact]
    public async Task ExploreRefusesAMovedCacheWhileAnEditorRuns()
    {
        var moved = Populate(Path.Combine(_temp.Path, "D", "Lighting"), "3f9a0c1e2b4d5f60.lz4");
        MoveCacheTo(moved);

        var provider = CreateProvider(new FakeProcessInspector("Unity"));
        var roots = await provider.DiscoverToolRootsAsync();

        Assert.Equal([Cache, moved], roots.Select(root => root.Path));
        Assert.All(roots, root => Assert.False(root.RecognisesFolder("3f")));
    }

    /// <summary>
    /// §7.1 reads the declaration: the GI cache may be removed from the Storage page, and Unity's folder,
    /// <c>Caches</c>, the build cache, the licence and anything unrecognised may not.
    /// </summary>
    [Theory]
    [InlineData(@"Caches\GiCache", true)]
    [InlineData(@"Caches\bee", false)]
    [InlineData(@"Caches\SomethingNew", false)]
    [InlineData("Caches", false)]
    [InlineData("licenses", false)]
    [InlineData("cache", false)]
    [InlineData("", false)]
    public void ExploreReadsUnitysFolderFromTheDeclaration(string relative, bool allowed)
    {
        CreateLayout();
        Directory.CreateDirectory(Path.Combine(Caches, "SomethingNew"));

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(relative.Length == 0 ? Unity : Path.Combine(Unity, relative)).IsAllowed);
    }

    /// <summary>§7.4: the places named without planning hold the cache the plan removes.</summary>
    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        CreateLayout();

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }
}
