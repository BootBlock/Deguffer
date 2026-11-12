using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The three rows that reclaim a Godot project's caches: the import cache and the shader cache inside
/// a Godot 4 project's data folder, and a Godot 3 project's import folder.
///
/// <para>What is particular to Godot is the folder around the caches. The data folder also holds the
/// export credentials and the editor's state, so it is never a target, and its name comes from the
/// project's settings rather than from a guess.</para>
/// </summary>
public sealed class GodotProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;

    public GodotProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    private string Git => _environment.FindExecutable("git")!;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task PlansTheImportCacheAndTheShaderCacheAsSeparateRows()
    {
        var data = GodotProjectFixture.CreateProject(Path.Combine(ApproveRoot(), "Platformer"));

        var imported = await Imported().PlanAsync();
        var shaders = await Shaders().PlanAsync();

        Assert.Equal([Path.Combine(data, "imported")], imported.TargetedPaths);
        Assert.Equal([Path.Combine(data, "shader_cache")], shaders.TargetedPaths);
        Assert.Equal(SafetyTier.RegenerableWithCost, imported.Tier);
        Assert.Equal(SafetyTier.RegenerableCache, shaders.Tier);
        Assert.True(imported.EstimatedBytes > 0);
        Assert.True(shaders.EstimatedBytes > 0);
    }

    /// <summary>
    /// §5.6, run for real. Both rows remove their cache from one project, and everything else survives:
    /// the data folder, the export credentials above all, the editor's state, the script class cache,
    /// the C# build output, the project file and every asset with its import settings.
    /// </summary>
    [Fact]
    public async Task EverythingButTheTwoCachesSurvivesBothRemovals()
    {
        var project = Path.Combine(ApproveRoot(), "Platformer");
        var data = GodotProjectFixture.CreateProject(project);

        string[] survivors =
        [
            data,
            project,
            .. GodotProjectFixture.DataFolderSurvivors.Select(name => Path.Combine(data, name)),
            Path.Combine(project, "project.godot"),
        ];

        foreach (var provider in new BuildDirectoryProvider[] { Imported(), Shaders() })
        {
            var plan = await provider.PlanAsync();

            foreach (var survivor in survivors)
            {
                Assert.Contains(plan.ProtectedPaths, p =>
                    p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
            }

            Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
            Assert.True((await provider.VerifyAsync(plan)).Passed);
        }

        Assert.False(Directory.Exists(Path.Combine(data, "imported")));
        Assert.False(Directory.Exists(Path.Combine(data, "shader_cache")));
        Assert.True(File.Exists(Path.Combine(data, "export_credentials.cfg")));
        Assert.True(File.Exists(Path.Combine(data, "editor", "filesystem_cache10")));
        Assert.True(File.Exists(Path.Combine(data, "mono", "temp", "bin", "Game.dll")));

        foreach (var file in GodotProjectFixture.ProjectSurvivors)
        {
            Assert.True(File.Exists(Path.Combine(project, file)), file);
        }
    }

    /// <summary>
    /// One row never names the other's cache as a survivor, so a run that ticks both does not report
    /// the second removal as a §5.6 failure of the first.
    /// </summary>
    [Fact]
    public async Task NeitherRowAssertsTheOthersCache()
    {
        var data = GodotProjectFixture.CreateProject(Path.Combine(ApproveRoot(), "Platformer"));

        var imported = await Imported().PlanAsync();
        var shaders = await Shaders().PlanAsync();

        Assert.DoesNotContain(imported.ProtectedPaths, p => p.Path.Equals(Path.Combine(data, "shader_cache"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(shaders.ProtectedPaths, p => p.Path.Equals(Path.Combine(data, "imported"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A project with the hidden folder turned off keeps its data in <c>godot</c>, and that folder's
    /// caches are offered. A <c>.godot</c> beside it is not the folder the settings name: it may be one
    /// an older setting left, and it is left alone.
    /// </summary>
    [Fact]
    public async Task TheDataFolderIsTheOneTheProjectsSettingsName()
    {
        var root = ApproveRoot();
        var renamed = GodotProjectFixture.CreateProject(Path.Combine(root, "Renamed"), hiddenDataFolder: false);
        var stale = GodotProjectFixture.CreateProject(Path.Combine(root, "Stale"), hiddenDataFolder: false, dataFolderName: ".godot");
        var mismatched = GodotProjectFixture.CreateProject(Path.Combine(root, "Mismatched"), dataFolderName: "godot");

        var plan = await Imported().PlanAsync();

        Assert.Equal([Path.Combine(renamed, "imported")], plan.TargetedPaths);

        foreach (var declined in new[] { stale, mismatched })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(declined, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// §5.2 inside the data folder. An import cache holding one file the importer does not write is
    /// declined and named, and the shader cache beside it is still offered on its own row.
    /// </summary>
    [Fact]
    public async Task AnImportCacheHoldingAStrangerIsLeftAloneAndTheShaderCacheIsStillOffered()
    {
        var data = GodotProjectFixture.CreateProject(Path.Combine(ApproveRoot(), "Platformer"));
        var imported = Path.Combine(data, "imported");
        File.WriteAllText(Path.Combine(imported, "notes.txt"), "kept by hand");

        var provider = Imported();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(imported, StringComparison.OrdinalIgnoreCase)
            && p.Reason.Contains("importer does not write", StringComparison.Ordinal));
        Assert.Equal([Path.Combine(data, "shader_cache")], (await Shaders().PlanAsync()).TargetedPaths);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(Path.Combine(imported, "notes.txt")));
    }

    [Fact]
    public async Task AShaderCacheHoldingAStrangerIsLeftAlone()
    {
        var data = GodotProjectFixture.CreateProject(Path.Combine(ApproveRoot(), "Platformer"));
        var shaders = Path.Combine(data, "shader_cache");
        GodotProjectFixture.WriteShaderVariant(shaders, "readme.txt");

        var plan = await Shaders().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(shaders, StringComparison.OrdinalIgnoreCase)
            && p.Reason.Contains("renderer does not write", StringComparison.Ordinal));
    }

    /// <summary>
    /// A cache that is a link is never offered: removing it would take only the link, but what it
    /// points at was never examined, and the plan says so rather than leaving it unmentioned.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task AnImportCacheThatIsALinkIsLeftAlone(DirectoryLinkKind kind)
    {
        var data = GodotProjectFixture.CreateProject(Path.Combine(ApproveRoot(), "Platformer"));
        var imported = Path.Combine(data, "imported");
        var elsewhere = _temp.CreateDirectory("elsewhere");
        GodotProjectFixture.WriteImportedAsset(elsewhere);
        Directory.Delete(imported, recursive: true);
        DirectoryLink.Create(kind, imported, elsewhere);

        var plan = await Imported().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(imported, StringComparison.OrdinalIgnoreCase)
            && p.Reason.StartsWith("A link", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(elsewhere, $"{GodotProjectFixture.ImportedBase}.ctex")));
    }

    /// <summary>
    /// A Godot 3 project's import folder is the whole of its data folder, and it is removed whole,
    /// leaving the project file and every asset's import settings.
    /// </summary>
    [Fact]
    public async Task AGodot3ImportFolderIsRemovedWholeAndTheProjectSurvives()
    {
        var project = Path.Combine(ApproveRoot(), "Classic");
        var import = GodotProjectFixture.CreateGodot3Project(project);

        var provider = Godot3();
        var plan = await provider.PlanAsync();

        Assert.Equal([import], plan.TargetedPaths);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(project, "project.godot"), StringComparison.OrdinalIgnoreCase));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True((await provider.VerifyAsync(plan)).Passed);
        Assert.False(Directory.Exists(import));

        foreach (var file in GodotProjectFixture.ProjectSurvivors)
        {
            Assert.True(File.Exists(Path.Combine(project, file)), file);
        }
    }

    /// <summary>
    /// The engine version decides which folder is the data folder. A <c>.import</c> an upgrade left in
    /// a Godot 4 project is not Godot 4's, and a <c>.godot</c> in a Godot 3 project is nobody's that
    /// the settings name. Each is left alone by every row.
    /// </summary>
    [Fact]
    public async Task EachRowTakesOnlyTheFolderItsEngineVersionWrites()
    {
        var root = ApproveRoot();
        var upgraded = Path.Combine(root, "Upgraded");
        GodotProjectFixture.CreateProject(upgraded);
        var leftover = GodotProjectFixture.CreateGodot3Project(Path.Combine(root, "Upgraded"), configVersion: 5);
        var godot3 = GodotProjectFixture.CreateProject(Path.Combine(root, "Classic"), configVersion: 4);

        Assert.DoesNotContain(leftover, (await Godot3().PlanAsync()).TargetedPaths);
        Assert.DoesNotContain(Path.Combine(godot3, "imported"), (await Imported().PlanAsync()).TargetedPaths);
        Assert.Equal([Path.Combine(upgraded, ".godot", "imported")], (await Imported().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// A data folder git tracks a file in is somebody's committed work, such as a test project inside
    /// the engine's own repository, and neither row offers anything from it.
    /// </summary>
    [Fact]
    public async Task LeavesADataFolderGitTracksAFileInAlone()
    {
        var repository = Path.Combine(ApproveRoot(), "engine");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var committed = GodotProjectFixture.CreateProject(Path.Combine(repository, "tests", "project"));
        var ignored = GodotProjectFixture.CreateProject(Path.Combine(repository, "demos", "project"));

        _environment.WithExecutable("git");
        var runner = new FakeProcessRunner().Responding(Git, "ls-files", "tests/project/.godot/uid_cache.bin\0");

        var plan = await Imported(runner).PlanAsync();

        Assert.Equal([Path.Combine(ignored, "imported")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(committed, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("tracked in git", StringComparison.Ordinal));
    }

    /// <summary>
    /// The editor works in the project it has open, so the live-tree veto holds the project back, and
    /// neither row offers anything from it while the editor runs.
    /// </summary>
    [Fact]
    public async Task NothingIsOfferedFromAProjectAnEditorHasOpen()
    {
        var root = ApproveRoot();
        var open = Path.Combine(root, "Open");
        GodotProjectFixture.CreateProject(open);
        var closed = GodotProjectFixture.CreateProject(Path.Combine(root, "Closed"));

        var editor = new FakeLiveTreeInspector()
            .WithProgram("Godot_v4.5-stable_win64", Path.Combine(_temp.Path, "tools", "Godot_v4.5-stable_win64.exe"), open);

        Assert.Equal([Path.Combine(closed, "imported")], (await Imported(live: editor).PlanAsync()).TargetedPaths);
        Assert.Equal([Path.Combine(closed, "shader_cache")], (await Shaders(live: editor).PlanAsync()).TargetedPaths);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryCacheThePlansRemove()
    {
        var root = ApproveRoot();
        GodotProjectFixture.CreateProject(Path.Combine(root, "team", "Platformer"));
        GodotProjectFixture.CreateGodot3Project(Path.Combine(root, "team", "Classic"));

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(Imported()));
        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(Shaders()));
        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(Godot3()));
    }

    [Fact]
    public void AllThreeRowsAreShipped()
    {
        using var shipped = new ShippedProviders();

        Assert.Single(shipped.Providers, p => p is GodotImportCacheProvider);
        Assert.Single(shipped.Providers, p => p is GodotShaderCacheProvider);
        Assert.Single(shipped.Providers, p => p is Godot3ImportCacheProvider);
    }

    private string ApproveRoot()
    {
        var root = _temp.CreateDirectory("src");
        _roots.Save([new SourceRoot(root)]);
        return root;
    }

    private GodotImportCacheProvider Imported(FakeProcessRunner? runner = null, ILiveTreeInspector? live = null)
    {
        var scanner = new FakeDirectoryScanner();

        return new GodotImportCacheProvider(
            _roots, new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()), live ?? FakeLiveTreeInspector.NothingLive,
            _environment, runner ?? new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner);
    }

    private GodotShaderCacheProvider Shaders(ILiveTreeInspector? live = null)
    {
        var scanner = new FakeDirectoryScanner();

        return new GodotShaderCacheProvider(
            _roots, new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()), live ?? FakeLiveTreeInspector.NothingLive,
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner);
    }

    private Godot3ImportCacheProvider Godot3()
    {
        var scanner = new FakeDirectoryScanner();

        return new Godot3ImportCacheProvider(
            _roots, new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()), FakeLiveTreeInspector.NothingLive,
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner);
    }
}
