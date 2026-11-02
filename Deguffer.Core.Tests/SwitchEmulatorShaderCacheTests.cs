using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Nintendo Switch emulators keep each game's caches inside a folder of its own, beside the
/// guest shader cache that only playing builds, so these are mostly negative tests: a game's folder is
/// entered only by its title id, and inside it only the caches compiled for this machine go.
/// </summary>
public sealed class SwitchEmulatorShaderCacheTests : IDisposable
{
    private const string Game = "0100f2c0115b6000";

    private const string OtherGame = "01007ef00011e000";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public SwitchEmulatorShaderCacheTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        Directory.CreateDirectory(_environment.Documents!);
    }

    public void Dispose() => _temp.Dispose();

    private string RyujinxRoot => Path.Combine(_environment.RoamingAppData, "Ryujinx");

    private string EdenRoot => Path.Combine(_environment.RoamingAppData, "eden");

    private string CitronRoot => Path.Combine(_environment.RoamingAppData, "citron");

    private string YuzuRoot => Path.Combine(_environment.RoamingAppData, "yuzu");

    private EmulatorShaderCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            system: _system,
            volumes: new FakeVolumeInventory());

    private void Declare(params string[] folders) => Assert.True(new EmulatorFolderStore(_environment).Save(folders));

    private static string WriteFile(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static string Folder(string path)
    {
        WriteFile(Path.Combine(path, "entry.bin"));
        return path;
    }

    private static string RyujinxGame(string root, string game = Game) => Path.Combine(root, "games", game);

    private static string Ryujinx(string root)
    {
        WriteFile(Path.Combine(root, "Config.json"), 64);
        return root;
    }

    /// <summary>A yuzu-family data folder, proven by the settings file its Qt front end writes.</summary>
    private static string YuzuFamily(string root)
    {
        WriteFile(Path.Combine(root, "config", "qt-config.ini"), 64);
        return root;
    }

    private static bool Holds(IEnumerable<ProtectedPath> paths, string path) =>
        paths.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static bool Allows(IReadOnlyList<ToolRoot> roots, string folder, string name, ChildKind kind) =>
        roots.Where(root => root.Path.Equals(folder, StringComparison.OrdinalIgnoreCase))
            .Any(root => root.Recognises(new ToolRootChild(name, kind)));

    /// <summary>
    /// The host cache and the PPTC cache go. The guest and shared files, built only by playing, stay,
    /// with the game's settings, the keys and the emulated storage beside them.
    /// </summary>
    [Fact]
    public async Task TakesRyujinxsHostAndCodeCachesAndKeepsTheGuestCache()
    {
        Ryujinx(RyujinxRoot);
        var game = RyujinxGame(RyujinxRoot);
        var shader = Path.Combine(game, "cache", "shader");
        var host = WriteFile(Path.Combine(shader, "vulkan_nvidia.toc"));
        var hostData = WriteFile(Path.Combine(shader, "vulkan_nvidia.data"));
        var dottedVendor = WriteFile(Path.Combine(shader, "opengl_ati.technologies.data"));
        var actual = Folder(Path.Combine(game, "cache", "cpu", "0"));
        var backup = Folder(Path.Combine(game, "cache", "cpu", "1"));

        var guest = WriteFile(Path.Combine(shader, "guest.data"));
        var shared = WriteFile(Path.Combine(shader, "shared.toc"));
        var unknown = WriteFile(Path.Combine(shader, "notes.txt"));
        var oldLayout = Folder(Path.Combine(shader, "program"));
        var strayCpu = Folder(Path.Combine(game, "cache", "cpu", "2"));
        var gameSettings = WriteFile(Path.Combine(game, "Config.json"));
        var gui = Folder(Path.Combine(game, "gui"));
        var keys = WriteFile(Path.Combine(RyujinxRoot, "system", "prod.keys"));
        var nand = Folder(Path.Combine(RyujinxRoot, "bis", "user", "save"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { host, hostData, dottedVendor, actual, backup }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var survivor in new[] { RyujinxRoot, game, shader, guest, shared, unknown, oldLayout, strayCpu, gameSettings, gui })
        {
            Assert.True(Holds(plan.ProtectedPaths, survivor), $"{survivor} is not asserted to survive");
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(host));
        Assert.False(Directory.Exists(actual));

        foreach (var file in new[] { guest, shared, unknown, gameSettings, keys })
        {
            Assert.True(File.Exists(file), $"{file} was removed");
        }

        foreach (var folder in new[] { oldLayout, strayCpu, gui, nand })
        {
            Assert.True(Directory.Exists(folder), $"{folder} was removed");
        }

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Only a folder named by a title id is a game's. Anything else in <c>games</c> is never entered,
    /// whatever it holds.
    /// </summary>
    [Fact]
    public async Task NeverEntersAFolderInRyujinxsGamesThatIsNotAGames()
    {
        Ryujinx(RyujinxRoot);
        var notAGame = WriteFile(Path.Combine(RyujinxRoot, "games", "backup", "cache", "shader", "vulkan_nvidia.toc"));
        var shortId = WriteFile(Path.Combine(RyujinxRoot, "games", "0100f2c0115b600", "cache", "shader", "vulkan_nvidia.toc"));
        var real = WriteFile(Path.Combine(RyujinxGame(RyujinxRoot), "cache", "shader", "vulkan_amd.toc"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([real], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(notAGame));
        Assert.True(File.Exists(shortId));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task FindsTheRyujinxPortableFolderInsideADeclaredOne()
    {
        var install = Path.Combine(_temp.Path, "emulators", "ryujinx");
        var root = Ryujinx(Path.Combine(install, "portable"));
        var host = WriteFile(Path.Combine(RyujinxGame(root), "cache", "shader", "vulkan_intel.toc"));
        Declare(install);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([host], plan.TargetedPaths);
    }

    /// <summary>
    /// A link at any level between the root and a cache folder is declined, not walked: otherwise the
    /// deletion lands somewhere nothing classified, and every survivor named below it with it.
    /// </summary>
    [Theory]
    [InlineData("games")]
    [InlineData("games\\" + Game)]
    [InlineData("games\\" + Game + "\\cache")]
    public async Task ALinkOnTheWayToARyujinxCacheIsNotFollowed(string linked)
    {
        Ryujinx(RyujinxRoot);
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        var below = Path.GetRelativePath(Path.Combine(RyujinxRoot, linked), Path.Combine(RyujinxGame(RyujinxRoot), "cache", "shader"));
        var beyond = WriteFile(Path.Combine(elsewhere, below, "vulkan_nvidia.toc"));
        var link = Path.Combine(RyujinxRoot, linked);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        SymbolicLink.ToDirectory(link, elsewhere);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.True(Holds(plan.ProtectedPaths, link));
        Assert.Single(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(beyond));
    }

    /// <summary>
    /// The driver's pipeline cache goes, for each game and Eden's own. The guest caches it is compiled
    /// from stay, and so does the old-layout folder a migration leaves, which Eden never reads.
    /// </summary>
    [Fact]
    public async Task TakesEdensPipelineCachesAndKeepsTheGuestCaches()
    {
        YuzuFamily(EdenRoot);
        var shaders = Path.Combine(EdenRoot, "cache", "shader");
        var pipelines = WriteFile(Path.Combine(shaders, Game, "vulkan_pipelines.bin"));
        var otherPipelines = WriteFile(Path.Combine(shaders, OtherGame, "vulkan_pipelines.bin"));
        var ownPipelines = WriteFile(Path.Combine(shaders, "vulkan_static_pipelines.bin"));

        var vulkan = WriteFile(Path.Combine(shaders, Game, "vulkan.bin"));
        var opengl = WriteFile(Path.Combine(shaders, Game, "opengl.bin"));
        var stray = WriteFile(Path.Combine(shaders, "readme.txt"));
        var migrated = WriteFile(Path.Combine(EdenRoot, "shader", Game, "vulkan_pipelines.bin"));
        var gameList = Folder(Path.Combine(EdenRoot, "cache", "game_list"));
        var nand = Folder(Path.Combine(EdenRoot, "nand", "user", "save"));
        var keys = WriteFile(Path.Combine(EdenRoot, "keys", "prod.keys"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { pipelines, otherPipelines, ownPipelines }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var survivor in new[] { EdenRoot, shaders, Path.Combine(shaders, Game), vulkan, opengl, stray })
        {
            Assert.True(Holds(plan.ProtectedPaths, survivor), $"{survivor} is not asserted to survive");
        }

        // A game's folder is answered for by its own caches, never named as left alone beside them.
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains(Game, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(pipelines));
        Assert.False(File.Exists(ownPipelines));

        foreach (var file in new[] { vulkan, opengl, stray, migrated, keys })
        {
            Assert.True(File.Exists(file), $"{file} was removed");
        }

        Assert.True(Directory.Exists(gameList));
        Assert.True(Directory.Exists(nand));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Citron and yuzu keep each game's cache in <c>shader</c>. Citron's <c>shader\nvidia</c> is the
    /// NVIDIA driver's own folder, not a game's, so it is never entered.
    /// </summary>
    [Fact]
    public async Task TakesCitronsAndYuzusPipelineCachesFromTheirOwnLayout()
    {
        YuzuFamily(CitronRoot);
        var citron = WriteFile(Path.Combine(CitronRoot, "shader", Game, "vulkan_pipelines.bin"));
        var citronGuest = WriteFile(Path.Combine(CitronRoot, "shader", Game, "vulkan.bin"));
        var nvidia = WriteFile(Path.Combine(CitronRoot, "shader", "nvidia", "GLCache", "vulkan_pipelines.bin"));
        var notEdens = WriteFile(Path.Combine(CitronRoot, "shader", "vulkan_static_pipelines.bin"));

        YuzuFamily(YuzuRoot);
        var yuzu = WriteFile(Path.Combine(YuzuRoot, "shader", OtherGame, "vulkan_pipelines.bin"));
        var yuzuGuest = WriteFile(Path.Combine(YuzuRoot, "shader", OtherGame, "opengl.bin"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { citron, yuzu }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        foreach (var file in new[] { citronGuest, nvidia, notEdens, yuzuGuest })
        {
            Assert.True(File.Exists(file), $"{file} was removed");
        }

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Every fork's portable folder is <c>user</c> with the same settings file, so the program beside
    /// it decides whose it is. An Eden folder read as Citron's would offer the old-layout cache Eden
    /// never reads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APortableYuzuFamilyFolderIsTheForkWhoseProgramIsBesideIt(bool declareTheDataFolder)
    {
        var install = Path.Combine(_temp.Path, "emulators", "eden");
        WriteFile(Path.Combine(install, "eden.exe"), 64);
        var root = YuzuFamily(Path.Combine(install, "user"));
        var pipelines = WriteFile(Path.Combine(root, "cache", "shader", Game, "vulkan_pipelines.bin"));
        var migrated = WriteFile(Path.Combine(root, "shader", Game, "vulkan_pipelines.bin"));
        Declare(declareTheDataFolder ? root : install);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([pipelines], plan.TargetedPaths);
        Assert.Equal(["Eden"], plan.Steps.Select(step => step.Group).Distinct());

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(migrated));
    }

    /// <summary>
    /// Citron unpacked over an old yuzu keeps both programs beside one <c>user</c> folder, which both
    /// forks prove and read. Its cache is one item, and while either fork runs it is held back on
    /// every route: the plan, the clean's last check and Explore.
    /// </summary>
    [Fact]
    public async Task AFolderTwoForksShareIsOneItemHeldWhileEitherRuns()
    {
        var install = Path.Combine(_temp.Path, "emulators", "citron");
        WriteFile(Path.Combine(install, "citron.exe"), 64);
        WriteFile(Path.Combine(install, "yuzu.exe"), 64);
        var root = YuzuFamily(Path.Combine(install, "user"));
        var game = Path.Combine(root, "shader", Game);
        var pipelines = WriteFile(Path.Combine(game, "vulkan_pipelines.bin"));
        var notCache = Folder(Path.Combine(game, "backup"));
        Declare(install);

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var offered = await provider.PlanAsync();

        Assert.Equal([pipelines], offered.TargetedPaths);
        Assert.Single(offered.Notes, n => n.Message.Contains(notCache, StringComparison.OrdinalIgnoreCase));

        var preview = await provider.PlanAsync();
        // yuzu rather than Citron: the item is Citron's by the order the layouts are read in, so only
        // the other fork tells a check of every owner from a check of the first.
        inspector.WithRunning("yuzu");
        var result = await provider.ExecuteAsync(preview);

        Assert.True(File.Exists(pipelines), "a shared cache was removed under a yuzu started after the preview");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);

        provider.InvalidateCaches();
        var held = await provider.PlanAsync();

        Assert.Empty(held.Steps);
        Assert.Contains(held.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("yuzu", StringComparison.Ordinal));
        Assert.False(Allows(await provider.DiscoverToolRootsAsync(), game, "vulkan_pipelines.bin", ChildKind.File));
    }

    /// <summary>
    /// Eden's <c>cache\shader</c> is both its folder of games and the folder of its own pipeline cache,
    /// so a refusal to list it is met twice, and is one thing to say.
    /// </summary>
    [Fact]
    public async Task AnEdenShaderFolderThatWillNotBeListedIsReportedOnce()
    {
        YuzuFamily(EdenRoot);
        var shaders = Path.Combine(EdenRoot, "cache", "shader");
        WriteFile(Path.Combine(shaders, Game, "vulkan_pipelines.bin"));
        using var denied = new DeniedDirectory(shaders);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Single(plan.Notes, n => n.Message.Contains(shaders, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ADeclaredUserFolderWithNoForksProgramBesideItIsNoEmulators()
    {
        var root = YuzuFamily(Path.Combine(_temp.Path, "somewhere", "user"));
        var pipelines = WriteFile(Path.Combine(root, "shader", Game, "vulkan_pipelines.bin"));
        Declare(root);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("found no", StringComparison.Ordinal));
        Assert.True(File.Exists(pipelines));
    }

    /// <summary>
    /// Eden's migration can replace its folder with a junction to an old yuzu or Citron folder. The
    /// link is declined, and the folder it points at is read once, as its own fork's.
    /// </summary>
    [Fact]
    public async Task AnEdenFolderLinkedToAnOldForksIsNotReadThroughTheLink()
    {
        YuzuFamily(CitronRoot);
        var citron = WriteFile(Path.Combine(CitronRoot, "shader", Game, "vulkan_pipelines.bin"));
        var edenInCitron = WriteFile(Path.Combine(CitronRoot, "cache", "shader", Game, "vulkan_pipelines.bin"));
        SymbolicLink.ToDirectory(EdenRoot, CitronRoot);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([citron], plan.TargetedPaths);
        Assert.True(Holds(plan.ProtectedPaths, EdenRoot));
        Assert.Contains(plan.Notes, n => n.Message.Contains(EdenRoot, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(edenInCitron));
    }

    [Fact]
    public async Task AGamesFolderThatIsALinkIsNamedAndNotDeletedThrough()
    {
        YuzuFamily(EdenRoot);
        var real = WriteFile(Path.Combine(EdenRoot, "cache", "shader", OtherGame, "vulkan_pipelines.bin"));
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        var beyond = WriteFile(Path.Combine(elsewhere, "vulkan_pipelines.bin"));
        var link = Path.Combine(EdenRoot, "cache", "shader", Game);
        SymbolicLink.ToDirectory(link, elsewhere);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([real], plan.TargetedPaths);
        Assert.True(Holds(plan.ProtectedPaths, link));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(beyond));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2 at every level in Explore: the way down to each game's caches, and in each cache folder
    /// only what the plan recognised.
    /// </summary>
    [Fact]
    public async Task ExploreIsToldTheWayToEachGamesCachesAndNothingBeside()
    {
        Ryujinx(RyujinxRoot);
        var game = RyujinxGame(RyujinxRoot);
        WriteFile(Path.Combine(game, "cache", "shader", "vulkan_nvidia.toc"));
        WriteFile(Path.Combine(game, "cache", "shader", "guest.toc"));
        Folder(Path.Combine(game, "cache", "cpu", "0"));
        YuzuFamily(EdenRoot);
        var edenShaders = Path.Combine(EdenRoot, "cache", "shader");
        WriteFile(Path.Combine(edenShaders, Game, "vulkan_pipelines.bin"));
        WriteFile(Path.Combine(edenShaders, Game, "vulkan.bin"));
        WriteFile(Path.Combine(edenShaders, "vulkan_static_pipelines.bin"));

        var roots = await CreateProvider().DiscoverToolRootsAsync();

        Assert.True(Allows(roots, RyujinxRoot, "games", ChildKind.Folder));
        Assert.False(Allows(roots, RyujinxRoot, "bis", ChildKind.Folder));
        Assert.True(Allows(roots, Path.Combine(RyujinxRoot, "games"), Game, ChildKind.Folder));
        Assert.False(Allows(roots, Path.Combine(RyujinxRoot, "games"), "backup", ChildKind.Folder));
        Assert.True(Allows(roots, game, "cache", ChildKind.Folder));
        Assert.False(Allows(roots, game, "gui", ChildKind.Folder));
        Assert.False(Allows(roots, game, "Config.json", ChildKind.File));
        Assert.True(Allows(roots, Path.Combine(game, "cache", "shader"), "vulkan_nvidia.toc", ChildKind.File));
        Assert.False(Allows(roots, Path.Combine(game, "cache", "shader"), "guest.toc", ChildKind.File));
        Assert.True(Allows(roots, Path.Combine(game, "cache", "cpu"), "0", ChildKind.Folder));

        Assert.True(Allows(roots, edenShaders, Game, ChildKind.Folder));
        Assert.True(Allows(roots, edenShaders, "vulkan_static_pipelines.bin", ChildKind.File));
        Assert.False(Allows(roots, edenShaders, "readme.txt", ChildKind.File));
        Assert.True(Allows(roots, Path.Combine(edenShaders, Game), "vulkan_pipelines.bin", ChildKind.File));
        Assert.False(Allows(roots, Path.Combine(edenShaders, Game), "vulkan.bin", ChildKind.File));
    }

    /// <summary>§5.3: Ryujinx writes its caches while a game is played.</summary>
    [Fact]
    public async Task NothingOfRyujinxsIsOfferedWhileItRuns()
    {
        Ryujinx(RyujinxRoot);
        var host = WriteFile(Path.Combine(RyujinxGame(RyujinxRoot), "cache", "shader", "vulkan_nvidia.toc"));
        YuzuFamily(EdenRoot);
        var eden = WriteFile(Path.Combine(EdenRoot, "cache", "shader", Game, "vulkan_pipelines.bin"));

        var provider = CreateProvider(new FakeProcessInspector("Ryujinx"));
        var plan = await provider.PlanAsync();

        Assert.Equal([eden], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("Ryujinx", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(host));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }
}
