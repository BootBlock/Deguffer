using System.Text;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// DuckStation, PPSSPP and Vita3K each keep their compiled shaders in a folder beside things that are
/// the user's own: DuckStation's achievement record, PPSSPP's copies of game images and the user's
/// post-processing shaders, and Vita3K's texture packs. So these are mostly negative tests: only the
/// names each emulator gives its shader cache go, found from a folder its own file proves.
/// </summary>
public sealed class PlayStationEmulatorShaderCacheTests : IDisposable
{
    private const string DuckStationProgram = "duckstation-qt-x64-ReleaseLTCG.exe";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public PlayStationEmulatorShaderCacheTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        Directory.CreateDirectory(_environment.Documents!);
    }

    public void Dispose() => _temp.Dispose();

    private string DuckStationRoot => Path.Combine(_environment.LocalAppData, "DuckStation");

    private string PpssppRoot => Path.Combine(_environment.Documents!, "PPSSPP");

    private EmulatorShaderCacheProvider CreateProvider(FakeProcessInspector? inspector = null, IVolumeInventory? volumes = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            system: _system,
            volumes: volumes ?? new FakeVolumeInventory());

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

    private static string DuckStation(string root)
    {
        WriteFile(Path.Combine(root, "settings.ini"), 64);
        return root;
    }

    private static string Ppsspp(string root)
    {
        WriteFile(Path.Combine(root, "PSP", "SYSTEM", "ppsspp.ini"), 64);
        return Path.Combine(root, "PSP", "SYSTEM", "CACHE");
    }

    /// <summary>A Vita3K install: its program beside the settings file it writes there.</summary>
    private static string Vita3K(string folder)
    {
        WriteFile(Path.Combine(folder, "Vita3K.exe"), 64);
        WriteFile(Path.Combine(folder, "config.yml"), 64);
        return Path.Combine(folder, "cache", "shaders");
    }

    private static bool Holds(IEnumerable<ProtectedPath> paths, string path) =>
        paths.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static void AssertSurvived(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            Assert.True(File.Exists(path) || Directory.Exists(path), $"{path} was removed");
        }
    }

    /// <summary>
    /// The cache folder holds the user's achievement unlocks beside the shaders, so only the files each
    /// renderer writes go, and a pipeline cache's half-written temporary file stays with the rest.
    /// </summary>
    [Fact]
    public async Task TakesOnlyDuckStationsShaderFiles()
    {
        var cache = Path.Combine(DuckStation(DuckStationRoot), "cache");
        string[] shaderFiles =
        [
            WriteFile(Path.Combine(cache, "vulkan_shaders.idx")),
            WriteFile(Path.Combine(cache, "vulkan_shaders.bin")),
            WriteFile(Path.Combine(cache, "vulkan_pipelines.bin")),
            WriteFile(Path.Combine(cache, "d3d11_shaders_debug.idx")),
            WriteFile(Path.Combine(cache, "d3d12_pipelines_debug.bin")),
            WriteFile(Path.Combine(cache, "opengl_pipelines.bin")),
        ];

        var achievements = WriteFile(Path.Combine(cache, "achievements.db"));
        var gameList = WriteFile(Path.Combine(cache, "gamelist.cache"));
        var downloads = WriteFile(Path.Combine(cache, "http_cache.bin"));
        var halfWritten = WriteFile(Path.Combine(cache, "d3d12_pipelines.bin.AbCdEf"));
        var namedLikeOne = Folder(Path.Combine(cache, "vulkan_shaders.bin.d"));
        var directoryNamedLikeOne = Folder(Path.Combine(cache, "d3d11_shaders.bin", "inner"));
        var userShaders = WriteFile(Path.Combine(DuckStationRoot, "shaders", "reshade", "Shaders", "crt.fx"));
        var memoryCards = WriteFile(Path.Combine(DuckStationRoot, "memcards", "shared_card_1.mcd"));
        var bios = WriteFile(Path.Combine(DuckStationRoot, "bios", "scph5501.bin"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(shaderFiles.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["DuckStation"], plan.Steps.Select(step => step.Group).Distinct());

        foreach (var survivor in new[] { DuckStationRoot, cache, achievements, gameList, halfWritten, Path.GetDirectoryName(directoryNamedLikeOne)! })
        {
            Assert.True(Holds(plan.ProtectedPaths, survivor), $"{survivor} is not asserted to survive");
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(shaderFiles, file => Assert.False(File.Exists(file)));
        AssertSurvived([achievements, gameList, downloads, halfWritten, namedLikeOne, directoryNamedLikeOne, userShaders, memoryCards, bios]);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>An older install leaves its folder in Documents, which DuckStation still uses where it exists.</summary>
    [Fact]
    public async Task FindsDuckStationInDocumentsAndLocalApplicationDataAlike()
    {
        var current = WriteFile(Path.Combine(DuckStation(DuckStationRoot), "cache", "vulkan_pipelines.bin"));
        var older = WriteFile(Path.Combine(DuckStation(Path.Combine(_environment.Documents!, "DuckStation")), "cache", "d3d12_pipelines.bin"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(new[] { current, older }.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FollowsDuckStationsCacheFolderWhereItsSettingsMoveIt()
    {
        DuckStation(DuckStationRoot);
        File.WriteAllText(Path.Combine(DuckStationRoot, "settings.ini"), "[Main]\nSettingsVersion = 3\n\n[Folders]\nCache = fast\\cache\n");
        var moved = WriteFile(Path.Combine(DuckStationRoot, "fast", "cache", "vulkan_pipelines.bin"));
        var defaultFolder = WriteFile(Path.Combine(DuckStationRoot, "cache", "vulkan_pipelines.bin"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([moved], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(moved));
        Assert.True(File.Exists(defaultFolder));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// <c>settings.ini</c> is a name many programs write, so a declared folder is DuckStation's only
    /// where its program is there.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeclaredDuckStationFolderIsReadOnlyWithItsProgram(bool withProgram)
    {
        var install = DuckStation(Path.Combine(_temp.Path, "emulators", "duckstation"));
        var pipelines = WriteFile(Path.Combine(install, "cache", "vulkan_pipelines.bin"));

        if (withProgram)
        {
            WriteFile(Path.Combine(install, DuckStationProgram), 64);
        }

        Declare(install);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(withProgram ? [pipelines] : [], plan.TargetedPaths);
        Assert.Equal(!withProgram, plan.Notes.Any(n => n.Message.Contains(install, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// DuckStation and PCSX2 unpacked into one folder each prove it by a file of their own, and both
    /// name <c>vulkan_shaders.bin</c> in the <c>cache</c> they share. The folder is both emulators',
    /// so either one running holds the file back.
    /// </summary>
    [Theory]
    [InlineData("pcsx2-qt")]
    [InlineData("duckstation-qt-x64-ReleaseLTCG")]
    public async Task AFolderDuckStationAndPcsx2ShareIsHeldWhileEitherRuns(string running)
    {
        var install = DuckStation(Path.Combine(_temp.Path, "emulators", "playstation"));
        WriteFile(Path.Combine(install, DuckStationProgram), 64);
        WriteFile(Path.Combine(install, "pcsx2-qt.exe"), 64);
        WriteFile(Path.Combine(install, "portable.ini"), 0);
        WriteFile(Path.Combine(install, "inis", "PCSX2.ini"), 64);
        var shaders = WriteFile(Path.Combine(install, "cache", "vulkan_shaders.bin"));
        Declare(install);

        Assert.Equal([shaders], (await CreateProvider().PlanAsync()).TargetedPaths);

        var provider = CreateProvider(new FakeProcessInspector(running));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(shaders));
    }

    /// <summary>
    /// Both emulators' settings can send their cache to one folder, where each reads the names it
    /// shares with the other. The file is one item, held back while either emulator runs.
    /// </summary>
    [Theory]
    [InlineData("pcsx2-qt")]
    [InlineData("duckstation-qt-x64-ReleaseLTCG")]
    public async Task ACacheFolderBothEmulatorsSettingsNameIsHeldWhileEitherRuns(string running)
    {
        var shared = Path.Combine(_temp.Path, "fast", "shader-cache");
        var pipelines = WriteFile(Path.Combine(shared, "vulkan_pipelines.bin"));
        WriteFile(Path.Combine(DuckStationRoot, "settings.ini"), 0);
        File.WriteAllText(Path.Combine(DuckStationRoot, "settings.ini"), $"[Folders]\nCache = {shared}\n");
        var pcsx2 = Path.Combine(_environment.Documents!, "PCSX2");
        WriteFile(Path.Combine(pcsx2, "inis", "PCSX2.ini"), 0);
        File.WriteAllText(Path.Combine(pcsx2, "inis", "PCSX2.ini"), $"[Folders]\nCache = {shared}\n");

        var idle = await CreateProvider().PlanAsync();

        Assert.Equal([pipelines], idle.TargetedPaths);

        var provider = CreateProvider(new FakeProcessInspector(running));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(pipelines));
    }

    /// <summary>
    /// DuckStation's settings can name PCSX2's cache folder through a letter <c>subst</c> made for
    /// PCSX2's folder. The folder is one folder whatever its name, so what both recognise in it is one
    /// item, named as PCSX2's own folder names it, and held back while either emulator runs.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("pcsx2-qt")]
    [InlineData("duckstation-qt-x64-ReleaseLTCG")]
    public async Task ACacheFolderNamedThroughASubstitutedLetterIsOneItemHeldWhileEitherRuns(string? running)
    {
        var letter = UnmappedDriveLetter.Any();
        var pcsx2 = Path.Combine(_environment.Documents!, "PCSX2");
        WriteFile(Path.Combine(pcsx2, "inis", "PCSX2.ini"), 64);
        var pipelines = WriteFile(Path.Combine(pcsx2, "cache", "vulkan_pipelines.bin"));
        WriteFile(Path.Combine(DuckStationRoot, "settings.ini"), 0);
        File.WriteAllText(Path.Combine(DuckStationRoot, "settings.ini"), $"[Folders]\nCache = {Path.Join(letter, "cache")}\n");

        var volumes = new FakeVolumeInventory().Substituting(letter, pcsx2);
        var provider = CreateProvider(running is null ? null : new FakeProcessInspector(running), volumes);
        var plan = await provider.PlanAsync();

        Assert.Equal(running is null ? [pipelines] : [], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.Equal(running is not null, File.Exists(pipelines));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.3, by any of the names DuckStation's builds give the program.</summary>
    [Theory]
    [InlineData("duckstation-qt-x64-ReleaseLTCG")]
    [InlineData("duckstation-qt-ARM64-ReleaseLTCG")]
    [InlineData("duckstation-qt-x64-ReleaseLTCG-SSE2")]
    public async Task NothingOfDuckStationsIsOfferedWhileItRuns(string program)
    {
        var pipelines = WriteFile(Path.Combine(DuckStation(DuckStationRoot), "cache", "vulkan_pipelines.bin"));

        var provider = CreateProvider(new FakeProcessInspector(program));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("DuckStation", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(pipelines));
    }

    /// <summary>
    /// A game's id is not always of one form, so the shader caches are recognised by their extension,
    /// and PPSSPP's other caches, the user's own shaders and the saves beside them stay.
    /// </summary>
    [Fact]
    public async Task TakesOnlyPpssppsShaderCaches()
    {
        var cache = Ppsspp(PpssppRoot);
        string[] shaderCaches =
        [
            WriteFile(Path.Combine(cache, "ULUS10041.vkshadercache")),
            WriteFile(Path.Combine(cache, "NPJH-50465.glshadercache")),
            WriteFile(Path.Combine(cache, "My Game.vkshadercache")),
            WriteFile(Path.Combine(cache, "vulkan_spirv.cache")),
        ];

        var imageCopy = WriteFile(Path.Combine(cache, "ULUS10041.ppdc"));
        var icons = WriteFile(Path.Combine(cache, "icon.cache"));
        var shares = Folder(Path.Combine(cache, "share", "1730000000"));
        var userShaders = WriteFile(Path.Combine(PpssppRoot, "PSP", "SHADERS", "crt.ini"));
        var saves = Folder(Path.Combine(PpssppRoot, "PSP", "SAVEDATA", "ULUS10041DATA00"));
        var states = WriteFile(Path.Combine(PpssppRoot, "PSP", "PPSSPP_STATE", "ULUS10041_1.00_0.ppst"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(shaderCaches.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var survivor in new[] { PpssppRoot, cache, imageCopy, icons, Path.Combine(cache, "share"), Path.Combine(PpssppRoot, "PSP", "SHADERS"), Path.Combine(PpssppRoot, "PSP", "SAVEDATA") })
        {
            Assert.True(Holds(plan.ProtectedPaths, survivor), $"{survivor} is not asserted to survive");
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(shaderCaches, file => Assert.False(File.Exists(file)));
        AssertSurvived([imageCopy, icons, shares, userShaders, saves, states]);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>An unpacked PPSSPP keeps its memory stick in <c>memstick</c> beside the program.</summary>
    [Fact]
    public async Task FindsThePpssppMemoryStickBesideADeclaredProgram()
    {
        var install = Path.Combine(_temp.Path, "emulators", "ppsspp");
        var shaders = WriteFile(Path.Combine(Ppsspp(Path.Combine(install, "memstick")), "ULUS10041.vkshadercache"));

        Assert.Empty((await CreateProvider().PlanAsync()).Steps);

        Declare(install);

        Assert.Equal([shaders], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// PPSSPP's settings write the memory stick's folder to <c>installed.txt</c>, after a byte order
    /// mark, and the installer's own copy holds a sentence that names no folder at all.
    /// </summary>
    [Fact]
    public async Task FindsThePpssppMemoryStickInstalledTxtNames()
    {
        var install = Path.Combine(_temp.Path, "emulators", "ppsspp");
        var memoryStick = Path.Combine(_temp.Path, "games", "psp");
        var shaders = WriteFile(Path.Combine(Ppsspp(memoryStick), "ULUS10041.vkshadercache"));
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "installed.txt"), memoryStick + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Declare(install);

        Assert.Equal([shaders], (await CreateProvider().PlanAsync()).TargetedPaths);

        File.WriteAllText(Path.Combine(install, "installed.txt"), "This gets renamed to \"installed.txt\" in the installer.\n");

        Assert.Empty((await CreateProvider().PlanAsync()).Steps);
    }

    /// <summary>
    /// Each game's folder in <c>cache\shaders</c> goes whole, as Vita3K's own command removes it, and
    /// nothing else in <c>cache</c> or beside it is reached.
    /// </summary>
    [Fact]
    public async Task TakesVita3KsCacheOneGameAtATime()
    {
        var install = Path.Combine(_temp.Path, "emulators", "vita3k");
        var shaders = Vita3K(install);
        var game = Folder(Path.Combine(shaders, "PCSE00001", "eboot.bin"));
        var homebrew = Folder(Path.Combine(shaders, "VITASHELL", "eboot.bin"));
        var notATitle = Folder(Path.Combine(shaders, "not-a-title"));
        var compatibility = WriteFile(Path.Combine(install, "cache", "app_compat_db.xml"));
        var decrypted = Folder(Path.Combine(install, "cache", "decrypted_selfs", "PCSE00001"));
        var textures = Folder(Path.Combine(install, "textures", "import", "PCSE00001"));
        var settings = Path.Combine(install, "config.yml");
        Declare(install);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { Path.GetDirectoryName(game)!, Path.GetDirectoryName(homebrew)! }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["Vita3K"], plan.Steps.Select(step => step.Group).Distinct());

        foreach (var survivor in new[] { install, shaders, notATitle, settings, Path.Combine(install, "textures"), Path.Combine(install, "cache", "decrypted_selfs") })
        {
            Assert.True(Holds(plan.ProtectedPaths, survivor), $"{survivor} is not asserted to survive");
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(game));
        AssertSurvived([notATitle, compatibility, decrypted, textures, settings]);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindsTheVita3KPortableFolderBesideItsProgram(bool declareThePortableFolder)
    {
        var install = Path.Combine(_temp.Path, "emulators", "vita3k");
        WriteFile(Path.Combine(install, "Vita3K.exe"), 64);
        var portable = Path.Combine(install, "portable");
        WriteFile(Path.Combine(portable, "config.yml"), 64);
        var game = Folder(Path.Combine(portable, "cache", "shaders", "PCSE00001"));
        Declare(declareThePortableFolder ? portable : install);

        Assert.Equal([game], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// Vita3K's <c>config.yml</c> is the name an older RPCS3 gave its settings, so without Vita3K's
    /// program beside it the folder is not Vita3K's. With it, the folder is Vita3K's alone: read as
    /// RPCS3's as well, its <c>cache\shaders</c> would be named as left alone while its games went,
    /// and Explore would refuse what the plan offers.
    /// </summary>
    [Fact]
    public async Task AVita3KFolderIsNotAlsoReadAsRpcs3s()
    {
        var install = Path.Combine(_temp.Path, "emulators", "vita3k");
        var shaders = Vita3K(install);
        var game = Folder(Path.Combine(shaders, "PCSE00001"));
        Declare(install);

        var provider = CreateProvider(new FakeProcessInspector("rpcs3"));
        var plan = await provider.PlanAsync();

        Assert.Equal([game], plan.TargetedPaths);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("RPCS3", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains(shaders + "'", StringComparison.OrdinalIgnoreCase));

        var explore = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.True(explore.MayRemove(game).IsAllowed);
        Assert.False(explore.MayRemove(Path.Combine(install, "cache", "decrypted_selfs")).IsAllowed);
        Assert.False(explore.MayRemove(Path.Combine(install, "textures")).IsAllowed);
    }

    /// <summary>
    /// RPCS3's current settings file is its own, so a folder holding it beside Vita3K's program is
    /// RPCS3's as well as Vita3K's, and each takes its own games' caches.
    /// </summary>
    [Fact]
    public async Task AFolderRpcs3ProvesByItsOwnFileStaysRpcs3sBesideVita3K()
    {
        var install = Path.Combine(_temp.Path, "emulators", "consoles");
        var vita = Folder(Path.Combine(Vita3K(install), "PCSE00001"));
        WriteFile(Path.Combine(install, "config", "config.yml"), 64);
        var ps3 = Folder(Path.Combine(install, "cache", "BLUS30443"));
        Declare(install);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(new[] { vita, ps3 }.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty((await CreateProvider(new FakeProcessInspector("rpcs3")).PlanAsync()).Steps);
    }

    /// <summary>Without its program, a folder holding <c>config.yml</c> is still read as an older RPCS3's.</summary>
    [Fact]
    public async Task AFolderWithConfigYmlAndNoVita3KIsStillRpcs3s()
    {
        var install = Path.Combine(_temp.Path, "emulators", "rpcs3");
        WriteFile(Path.Combine(install, "config.yml"), 64);
        var game = Folder(Path.Combine(install, "cache", "BLUS30443"));
        var vitaShaped = Folder(Path.Combine(install, "cache", "shaders", "PCSE00001"));
        Declare(install);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([game], plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(game));
        Assert.True(Directory.Exists(vitaShaped));
    }

    /// <summary>§5.3: Vita3K writes a game's shaders while it is played.</summary>
    [Fact]
    public async Task NothingOfVita3KsIsOfferedWhileItRuns()
    {
        var install = Path.Combine(_temp.Path, "emulators", "vita3k");
        var game = Folder(Path.Combine(Vita3K(install), "PCSE00001"));
        Declare(install);

        var provider = CreateProvider(new FakeProcessInspector("Vita3K"));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);

        var roots = await provider.DiscoverToolRootsAsync();
        Assert.DoesNotContain(roots, root => root.Recognises(new ToolRootChild("PCSE00001", ChildKind.Folder)));

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(game));
    }
}
