using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Deno's folder holds what Deno programs saved and the REPL history beside its caches, which is why
/// <c>deno clean</c> is not used and why most of these are about what survives. No Deno was installed
/// on the machine these were written on, so the layout is the one <c>deno_dir.rs</c> defines.
/// </summary>
public sealed class DenoCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public DenoCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private DenoCacheProvider CreateProvider(IProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    private string Root => Path.Combine(_environment.LocalAppData, "deno");

    private static string CreateFolder(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "payload.bin"), new byte[4096]);
        return path;
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4096]);
        return path;
    }

    /// <summary>What a Deno that has run, been upgraded and opened a REPL leaves, in <paramref name="root"/>.</summary>
    private static (IReadOnlyList<string> Disposable, IReadOnlyList<string> Kept) CreateLayout(string root)
    {
        List<string> disposable =
        [
            .. new[] { "remote", "npm", "gen", "registries", "dl" }.Select(name => CreateFolder(Path.Combine(root, name))),
            .. DenoCacheProvider.Databases.SelectMany(db => new[] { db, db + "-wal", db + "-shm" })
                .Select(name => CreateFile(Path.Combine(root, name))),
            CreateFile(Path.Combine(root, "latest.txt")),
        ];

        List<string> kept =
        [
            CreateFile(Path.Combine(root, "location_data", "0123abcd", "kv.sqlite3")),
            CreateFile(Path.Combine(root, "deno_history.txt")),
            CreateFolder(Path.Combine(root, "node_compat_bin")),
            CreateFile(Path.Combine(root, "dep_analysis_cache_v1")),
        ];

        return (disposable, kept);
    }

    [Fact]
    public async Task ReportsNotPresentWhenDenoHasNeverRun()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>A folder holding only what Deno programs saved has nothing to offer, so the row is not shown.</summary>
    [Fact]
    public async Task IsNotPresentWhereOnlyWhatProgramsSavedIsThere()
    {
        CreateFile(Path.Combine(Root, "location_data", "0123abcd", "local_storage"));
        CreateFile(Path.Combine(Root, "deno_history.txt"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// Every child <c>DenoDir</c> names as derived is offered, each database with its log. What Deno
    /// programs saved, the REPL history, a folder another Deno tool writes and a database at a version
    /// Deno no longer reads are asserted to survive (§5.6).
    /// </summary>
    [Fact]
    public async Task OffersWhatDenoDefinesAsDerivedAndKeepsWhatProgramsSaved()
    {
        var (disposable, kept) = CreateLayout(Root);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            disposable.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var survivor in new[] { Root, Path.Combine(Root, "location_data"), Path.Combine(Root, "deno_history.txt"), Path.Combine(Root, "node_compat_bin"), Path.Combine(Root, "dep_analysis_cache_v1") })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.All(disposable, path => Assert.False(Path.Exists(path), path));
        Assert.All(kept, path => Assert.True(Path.Exists(path), $"{path} was removed."));
    }

    /// <summary>
    /// The named survivors are asserted whatever the listing met, so a plan that has never seen
    /// <c>location_data</c> still says it must stand.
    /// </summary>
    [Fact]
    public async Task NamesTheSurvivorsEvenWhenTheyAreNotThere()
    {
        CreateFolder(Path.Combine(Root, "remote"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(Root, "location_data"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(Root, "deno_history.txt"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HonoursDenoDir()
    {
        var moved = Path.Combine(_temp.Path, "caches", "deno");
        var remote = CreateFolder(Path.Combine(moved, "remote"));
        CreateFolder(Path.Combine(Root, "remote"));
        _environment.WithEnvironmentVariable(DenoCacheProvider.DirectoryVariable, moved);

        Assert.Equal([remote], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>Deno honours the cache home on Windows too, and keeps its folder inside it rather than being it.</summary>
    [Fact]
    public async Task HonoursTheCacheHomeWhereDenoDirIsUnset()
    {
        var home = Path.Combine(_temp.Path, "profile", ".cache");
        var remote = CreateFolder(Path.Combine(home, "deno", "remote"));
        var bystander = CreateFolder(Path.Combine(home, "remote"));
        _environment.WithEnvironmentVariable(DenoCacheProvider.XdgCacheVariable, home);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([remote], plan.TargetedPaths);
        Assert.Equal(Path.Combine(home, "deno"), Assert.Single(provider.ToolRoots).Path);
        Assert.DoesNotContain(bystander, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DenoDirWinsOverTheCacheHome()
    {
        var moved = Path.Combine(_temp.Path, "caches", "deno");
        var remote = CreateFolder(Path.Combine(moved, "remote"));
        CreateFolder(Path.Combine(_temp.Path, "xdg", "deno", "remote"));
        _environment.WithEnvironmentVariable(DenoCacheProvider.DirectoryVariable, moved);
        _environment.WithEnvironmentVariable(DenoCacheProvider.XdgCacheVariable, Path.Combine(_temp.Path, "xdg"));

        Assert.Equal([remote], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>Deno joins a relative value to its own working directory, which Deguffer is not.</summary>
    [Theory]
    [InlineData(DenoCacheProvider.DirectoryVariable)]
    [InlineData(DenoCacheProvider.XdgCacheVariable)]
    public async Task OffersNothingWhenTheSettingIsRelative(string variable)
    {
        CreateFolder(Path.Combine(Root, "remote"));
        _environment.WithEnvironmentVariable(variable, @"..\cache");

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.ToolRoots);

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a full path", StringComparison.Ordinal)
            && n.Message.StartsWith($@"{variable} is set to '..\cache'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A folder a setting names and this declines is still where Deno keeps what its programs saved, so
    /// Explore refuses those entries there and nothing else. <c>DENO_DIR</c> naming the profile is
    /// declined for being the profile, and the <c>deno</c> folder in a cache home for holding the
    /// temporary folder.
    /// </summary>
    [Theory]
    [InlineData(DenoCacheProvider.DirectoryVariable)]
    [InlineData(DenoCacheProvider.XdgCacheVariable)]
    public async Task ADeclinedFolderStillRefusesWhatProgramsSaved(string variable)
    {
        string declined;

        if (variable == DenoCacheProvider.DirectoryVariable)
        {
            declined = _environment.UserProfile;
            _environment.WithEnvironmentVariable(variable, declined);
        }
        else
        {
            var home = Path.Combine(_environment.UserProfile, ".cache");
            declined = Path.Combine(home, "deno");
            _environment.WithTempPath(Directory.CreateDirectory(Path.Combine(declined, "tmp")).FullName);
            _environment.WithEnvironmentVariable(variable, home);
        }

        var provider = CreateProvider();

        Assert.Empty(provider.ToolRoots);
        Assert.True((await provider.PlanAsync()).IsEmpty);

        var root = Assert.Single(await provider.DiscoverToolRootsAsync());
        Assert.Equal(declined, root.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ToolRootClaim.NamedEntries, root.Claim);
        Assert.False(root.Recognises(new ToolRootChild("location_data", ChildKind.Folder)));
        Assert.False(root.Recognises(new ToolRootChild("deno_history.txt", ChildKind.File)));
        Assert.True(root.Recognises(new ToolRootChild("Documents", ChildKind.Folder)));
    }

    /// <summary>
    /// Deno holds its databases open while it runs, and its language server runs as long as an editor
    /// does, so a Deno started after the preview holds every step back.
    /// </summary>
    [Fact]
    public async Task ADenoStartedAfterThePreviewHoldsTheCleanBack()
    {
        var (disposable, _) = CreateLayout(Root);
        var inspector = new FakeProcessInspector();

        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        inspector.WithRunning("deno");
        var result = await provider.ExecuteAsync(plan);

        Assert.All(disposable, path => Assert.True(Path.Exists(path), $"{path} was removed while Deno ran."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task WarnsWhenDenoIsRunning()
    {
        CreateFolder(Path.Combine(Root, "remote"));

        var plan = await CreateProvider(new FakeProcessInspector("deno")).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("deno", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheToolRootRecognisesFoldersAndFilesByTheirKind()
    {
        var root = Assert.Single(CreateProvider().ToolRoots);

        Assert.True(root.Recognises(new ToolRootChild("remote", ChildKind.Folder)));
        Assert.False(root.Recognises(new ToolRootChild("remote", ChildKind.File)));
        Assert.True(root.Recognises(new ToolRootChild("check_cache_v2", ChildKind.File)));
        Assert.True(root.Recognises(new ToolRootChild("check_cache_v2-wal", ChildKind.File)));
        Assert.False(root.Recognises(new ToolRootChild("check_cache_v2", ChildKind.Folder)));
        Assert.False(root.Recognises(new ToolRootChild("location_data", ChildKind.Folder)));
        Assert.False(root.Recognises(new ToolRootChild("deno_history.txt", ChildKind.File)));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        CreateLayout(Root);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>A plan holds display paths; the I/O beneath it extends them where it acts (§6.3).</summary>
    [Fact]
    public async Task ThePlanHoldsDisplayPaths()
    {
        CreateLayout(Root);

        var plan = await CreateProvider().PlanAsync();

        Assert.NotEmpty(plan.TargetedPaths);
        Assert.All(plan.TargetedPaths, path => Assert.False(path.StartsWith(@"\\?\", StringComparison.Ordinal), path));
    }
}
