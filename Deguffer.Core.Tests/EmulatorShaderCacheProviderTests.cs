using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every emulator keeps its shader cache beside saves, memory cards, firmware and installed games,
/// so these are mostly negative tests: an unrecognised child here is somebody's save file, and
/// Dolphin's own <c>Shaders</c> folder shares a name with the cache that is the target.
/// </summary>
public sealed class EmulatorShaderCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public EmulatorShaderCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        Directory.CreateDirectory(_environment.Documents!);
    }

    public void Dispose() => _temp.Dispose();

    private string DolphinRoot => Path.Combine(_environment.RoamingAppData, "Dolphin Emulator");

    private string CemuRoot => Path.Combine(_environment.RoamingAppData, "Cemu");

    private string Pcsx2Root => Path.Combine(_environment.Documents!, "PCSX2");

    private EmulatorShaderCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    private void Declare(params string[] folders) => Assert.True(new EmulatorFolderStore(_environment).Save(folders));

    private static string WriteFile(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static string Folder(string path, int bytes = 4096)
    {
        WriteFile(Path.Combine(path, "entry.bin"), bytes);
        return path;
    }

    /// <summary>A Dolphin user folder, proven by its settings file, with the compiled cache in it.</summary>
    private string Dolphin(string root)
    {
        WriteFile(Path.Combine(root, "Config", "Dolphin.ini"), 64);
        return Folder(Path.Combine(root, "Cache", "Shaders"));
    }

    private static string Rpcs3(string root, string serial = "BLUS30443")
    {
        WriteFile(Path.Combine(root, "config", "config.yml"), 64);
        return Folder(Path.Combine(root, "cache", serial));
    }

    private static bool IsAtOrUnder(string ancestor, string path) => LongPath.Contains(ancestor, path);

    [Fact]
    public async Task ReportsNotPresentWhereNoEmulatorHasWrittenAnything()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public void IsTierTwoAndItsPartsAreSeparateDecisions()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);
        Assert.Equal(StepGrain.Parts, provider.Grain);
    }

    /// <summary>
    /// The collision the provider is written against: <c>Shaders</c> is the user's post-processing
    /// shaders and <c>Cache\Shaders</c> the compiled cache. Only the path from the root decides.
    /// </summary>
    [Fact]
    public async Task TakesDolphinsCompiledShadersAndNeverTheUsersOwn()
    {
        var cache = Dolphin(DolphinRoot);
        var userShaders = Folder(Path.Combine(DolphinRoot, "Shaders"));
        var memoryCards = Folder(Path.Combine(DolphinRoot, "GC"));
        var nand = Folder(Path.Combine(DolphinRoot, "Wii"));
        var gameList = WriteFile(Path.Combine(DolphinRoot, "Cache", "gamelist.cache"));
        var uidCache = WriteFile(Path.Combine(DolphinRoot, "Cache", "GALE01.uidcache"));
        var covers = Folder(Path.Combine(DolphinRoot, "Cache", "GameCovers"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([cache], plan.TargetedPaths);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);

        foreach (var survivor in new[] { DolphinRoot, userShaders, memoryCards, nand, gameList, uidCache, covers })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase));
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(cache));
        Assert.True(Directory.Exists(userShaders));
        Assert.True(Directory.Exists(memoryCards));
        Assert.True(Directory.Exists(nand));
        Assert.True(File.Exists(gameList));
        Assert.True(File.Exists(uidCache));
        Assert.True(Directory.Exists(covers));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A folder shaped like Dolphin's is not Dolphin's without the settings file it writes.</summary>
    [Fact]
    public async Task AFolderWithoutTheEmulatorsSettingsFileIsNotItsRoot()
    {
        var cache = Folder(Path.Combine(DolphinRoot, "Cache", "Shaders"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).Steps);
        Assert.True(Directory.Exists(cache));
    }

    [Fact]
    public async Task FindsDolphinWhereItsRegistryValueSendsIt()
    {
        var elsewhere = Path.Combine(_temp.Path, "games", "Dolphin User");
        var cache = Dolphin(elsewhere);
        _environment.WithRegistryValue(DolphinLayout.RegistryKey, DolphinLayout.RegistryValue, elsewhere);

        Assert.Equal([cache], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>An older install leaves a proven root in Documents beside the current one.</summary>
    [Fact]
    public async Task FindsDolphinInDocumentsAndApplicationDataAlike()
    {
        var current = Dolphin(DolphinRoot);
        var older = Dolphin(Path.Combine(_environment.Documents!, "Dolphin Emulator"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            new[] { current, older }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NothingIsLookedForInDocumentsWhenWindowsWillNotSayWhereItIs()
    {
        var cache = Dolphin(Path.Combine(_environment.Documents!, "Dolphin Emulator"));
        _environment.WithNoDocuments();

        Assert.DoesNotContain(cache, (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// <c>transferable</c> is hours of play or a download that may not be obtainable again, and the
    /// other two are compiled from it.
    /// </summary>
    [Fact]
    public async Task TakesCemusCompiledCachesAndKeepsTheTransferableOne()
    {
        WriteFile(Path.Combine(CemuRoot, "settings.xml"), 64);
        var precompiled = Folder(Path.Combine(CemuRoot, "shaderCache", "precompiled"));
        var driver = Folder(Path.Combine(CemuRoot, "shaderCache", "driver", "vk"));
        var transferable = Folder(Path.Combine(CemuRoot, "shaderCache", "transferable"));
        var saves = Folder(Path.Combine(CemuRoot, "mlc01"));
        var keys = WriteFile(Path.Combine(CemuRoot, "keys.txt"), 64);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { Path.GetDirectoryName(driver)!, precompiled }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains(transferable, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(transferable));
        Assert.True(Directory.Exists(saves));
        Assert.True(File.Exists(keys));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>RPCS3 has no fixed location, so without a declared folder it is not found at all.</summary>
    [Fact]
    public async Task FindsRpcs3OnlyInAFolderTheUserDeclared()
    {
        var install = Path.Combine(_temp.Path, "emulators", "rpcs3");
        var game = Rpcs3(install);

        Assert.Empty((await CreateProvider().PlanAsync()).Steps);

        Declare(install);

        Assert.Equal([game], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    [Fact]
    public async Task FindsTheRpcs3PortableFolderInsideADeclaredOne()
    {
        var install = Path.Combine(_temp.Path, "emulators", "rpcs3");
        var game = Rpcs3(Path.Combine(install, "portable"));
        Declare(install);

        Assert.Equal([game], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>RPCS3 cuts the variable back to its last separator, so a value without one names the parent.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsRpcs3sConfigurationVariableAsRpcs3Does(bool trailingSeparator)
    {
        var parent = Path.Combine(_temp.Path, "rpcs3-data");
        var inner = Path.Combine(parent, "inner");
        var atParent = Rpcs3(parent, "BLUS30443");
        var atInner = Rpcs3(inner, "NPEB00874");

        _environment.WithEnvironmentVariable(
            Rpcs3Layout.ConfigDirectoryVariable, trailingSeparator ? inner + Path.DirectorySeparatorChar : inner);

        Assert.Equal([trailingSeparator ? atInner : atParent], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// Only a folder named by a game's serial is one game's cache. <c>playlists</c> is soundtracks the
    /// user built, and anything else in <c>cache</c> is not offered.
    /// </summary>
    [Fact]
    public async Task TakesOnlyEachGamesCacheFromRpcs3()
    {
        var install = Path.Combine(_temp.Path, "emulators", "rpcs3");
        var game = Rpcs3(install);
        var playlists = Folder(Path.Combine(install, "cache", "playlists"));
        var firmware = Folder(Path.Combine(install, "cache", "vsh"));
        var notASerial = Folder(Path.Combine(install, "cache", "BLUS3044"));
        var loose = WriteFile(Path.Combine(install, "cache", "ppu-abc-libsre.sprx"));
        var saves = Folder(Path.Combine(install, "dev_hdd0", "home", "00000001", "savedata"));
        var firmwareInstall = Folder(Path.Combine(install, "dev_flash"));
        Declare(install);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([game], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        foreach (var survivor in new[] { playlists, firmware, notASerial, saves, firmwareInstall })
        {
            Assert.True(Directory.Exists(survivor), survivor);
        }

        Assert.True(File.Exists(loose));
        Assert.False(Directory.Exists(game));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// PCSX2 keeps downloaded fonts and the game list in its cache folder too, so only the shader
    /// files each renderer writes are taken, as files.
    /// </summary>
    [Fact]
    public async Task TakesOnlyPcsx2sShaderFiles()
    {
        WriteFile(Path.Combine(Pcsx2Root, "inis", "PCSX2.ini"), 64);
        var cache = Path.Combine(Pcsx2Root, "cache");
        string[] shaderFiles =
        [
            WriteFile(Path.Combine(cache, "vulkan_shaders.idx")),
            WriteFile(Path.Combine(cache, "vulkan_shaders.bin")),
            WriteFile(Path.Combine(cache, "vulkan_pipelines.bin")),
            WriteFile(Path.Combine(cache, "d3d_shaders_sm50.bin")),
            WriteFile(Path.Combine(cache, "d3d12_pipelines_sm60_debug.idx")),
            WriteFile(Path.Combine(cache, "gl_programs.bin")),
        ];
        var gameList = WriteFile(Path.Combine(cache, "gamelist.cache"));
        var fonts = Folder(Path.Combine(cache, "fonts"));
        var namedLikeAShaderFile = Folder(Path.Combine(cache, "vulkan_shaders.bin.d"));
        var memoryCards = Folder(Path.Combine(Pcsx2Root, "memcards"));
        var bios = Folder(Path.Combine(Pcsx2Root, "bios"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(shaderFiles.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan.Steps, step => Assert.IsType<DeleteFileStep>(step));

        var result = await provider.ExecuteAsync(plan);

        Assert.All(shaderFiles, file => Assert.False(File.Exists(file), file));
        Assert.True(File.Exists(gameList));
        Assert.True(Directory.Exists(fonts));
        Assert.True(Directory.Exists(namedLikeAShaderFile));
        Assert.True(Directory.Exists(memoryCards));
        Assert.True(Directory.Exists(bios));
        Assert.True(Directory.Exists(cache));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A directory carrying a shader file's name is not a shader file.</summary>
    [Fact]
    public async Task ADirectoryNamedLikeAPcsx2ShaderFileIsNotOne()
    {
        WriteFile(Path.Combine(Pcsx2Root, "inis", "PCSX2.ini"), 64);
        var directory = Folder(Path.Combine(Pcsx2Root, "cache", "vulkan_shaders.bin"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains(directory, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowsPcsx2sCacheFolderWhereItsSettingsMoveIt(bool absolute)
    {
        var moved = absolute ? Path.Combine(_temp.Path, "fast", "pcsx2-cache") : Path.Combine(Pcsx2Root, "moved");
        WriteFile(
            Path.Combine(Pcsx2Root, "inis", "PCSX2.ini"),
            0);
        File.WriteAllText(
            Path.Combine(Pcsx2Root, "inis", "PCSX2.ini"),
            $"[UI]\nCache = elsewhere\n\n[Folders]\nBios = bios\nCache = {(absolute ? moved : "moved")}\n");
        var shaders = WriteFile(Path.Combine(moved, "vulkan_shaders.bin"));
        var defaultFolder = WriteFile(Path.Combine(Pcsx2Root, "cache", "vulkan_shaders.bin"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([shaders], plan.TargetedPaths);
        Assert.DoesNotContain(defaultFolder, plan.TargetedPaths);
    }

    /// <summary>
    /// A setting can point the cache at any folder, and one holding the profile is not PCSX2's to
    /// answer for, whatever sits in it.
    /// </summary>
    [Fact]
    public async Task RefusesACacheFolderMovedOntoSomethingNoToolOwns()
    {
        File.WriteAllText(
            WriteFile(Path.Combine(Pcsx2Root, "inis", "PCSX2.ini"), 0),
            $"[Folders]\nCache = {_temp.Path}\n");
        var stray = WriteFile(Path.Combine(_temp.Path, "vulkan_shaders.bin"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains(_temp.Path, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(stray));
    }

    [Fact]
    public async Task FindsThePcsx2FolderPortableTxtNames()
    {
        var install = Path.Combine(_temp.Path, "emulators", "pcsx2");
        File.WriteAllText(WriteFile(Path.Combine(install, "portable.txt"), 0), "  data\r\n");
        WriteFile(Path.Combine(install, "data", "inis", "PCSX2.ini"), 64);
        var shaders = WriteFile(Path.Combine(install, "data", "cache", "gl_programs.idx"));
        Declare(install);

        Assert.Equal([shaders], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>A declared folder that holds no emulator is said so, rather than looked through.</summary>
    [Fact]
    public async Task ADeclaredFolderHoldingNoEmulatorIsSaidSo()
    {
        var folder = Folder(Path.Combine(_temp.Path, "not-an-emulator"));
        Folder(Path.Combine(folder, "cache", "BLUS30443"));
        Declare(folder);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A declared folder holding RetroArch is the RetroArch rows' to answer for, so this row does not
    /// tell the user it holds no emulator.
    /// </summary>
    [Fact]
    public async Task ADeclaredRetroArchFolderIsNotSaidToHoldNoEmulator()
    {
        var retroArch = new RetroArchFixture(_environment, Path.Combine(_temp.Path, "RetroArch-Win64")).Install();
        Declare(retroArch.Program);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.False(await provider.IsPresentAsync());
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains(retroArch.Program, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A settings file in a folder that holds the whole profile does not make the profile an
    /// emulator's: Explore would otherwise refuse everything in it but a cache.
    /// </summary>
    [Fact]
    public async Task AFolderHoldingTheProfileIsNeverAnEmulatorsRoot()
    {
        var game = Rpcs3(_temp.Path);
        Declare(_temp.Path);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(Directory.Exists(game));
        Assert.DoesNotContain(await provider.DiscoverToolRootsAsync(), r => r.Path.Equals(_temp.Path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.2 at every level: the root refuses everything but the way to its cache, and the cache folder
    /// everything but what the plan recognised.
    /// </summary>
    [Fact]
    public async Task ExploreIsToldTheWayToTheCacheAndNothingBesideIt()
    {
        Dolphin(DolphinRoot);
        Folder(Path.Combine(DolphinRoot, "Shaders"));
        Folder(Path.Combine(DolphinRoot, "Cache", "GameCovers"));

        var roots = await CreateProvider().DiscoverToolRootsAsync();

        var root = Assert.Single(roots, r => r.Path.Equals(DolphinRoot, StringComparison.OrdinalIgnoreCase));
        Assert.True(root.Recognises("Cache"));
        Assert.False(root.Recognises("Shaders"));
        Assert.False(root.Recognises("GC"));

        var cache = Assert.Single(roots, r => r.Path.Equals(Path.Combine(DolphinRoot, "Cache"), StringComparison.OrdinalIgnoreCase));
        Assert.True(cache.Recognises("Shaders"));
        Assert.False(cache.Recognises("GameCovers"));

        var userShaders = Assert.Single(roots, r => r.Path.Equals(Path.Combine(DolphinRoot, "Shaders"), StringComparison.OrdinalIgnoreCase));
        Assert.False(userShaders.Recognises("anything"));
    }

    /// <summary>§5.3: an emulator writes its cache while a game is played.</summary>
    [Fact]
    public async Task NothingOfAnEmulatorsIsOfferedWhileItRuns()
    {
        var dolphin = Dolphin(DolphinRoot);
        WriteFile(Path.Combine(CemuRoot, "settings.xml"), 64);
        var cemu = Folder(Path.Combine(CemuRoot, "shaderCache", "precompiled"));

        var provider = CreateProvider(new FakeProcessInspector("Dolphin"));
        var plan = await provider.PlanAsync();

        Assert.Equal([cemu], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("Dolphin", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(DolphinRoot, StringComparison.OrdinalIgnoreCase));

        var roots = await provider.DiscoverToolRootsAsync();
        Assert.False(Assert.Single(roots, r => r.Path.Equals(Path.Combine(DolphinRoot, "Cache"), StringComparison.OrdinalIgnoreCase))
            .Recognises("Shaders"));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(dolphin));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task AnEmulatorStartedAfterThePreviewHoldsItsCacheBack()
    {
        var cache = Dolphin(DolphinRoot);

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.Equal([cache], plan.TargetedPaths);

        inspector.WithRunning("DolphinNoGUI");
        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(cache), "a shader cache was removed under a Dolphin started after the preview");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task ACacheFolderThatIsALinkIsLeftAlone()
    {
        WriteFile(Path.Combine(DolphinRoot, "Config", "Dolphin.ini"), 64);
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        var shaders = Folder(Path.Combine(elsewhere, "Shaders"));
        SymbolicLink.ToDirectory(Path.Combine(DolphinRoot, "Cache"), elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));
        Assert.True(Directory.Exists(shaders));
    }

    [Fact]
    public async Task AGameCacheThatIsALinkIsNamedAndNotDeletedThrough()
    {
        var install = Path.Combine(_temp.Path, "emulators", "rpcs3");
        var real = Rpcs3(install);
        var elsewhere = Folder(Path.Combine(_temp.Path, "elsewhere"));
        var link = Path.Combine(install, "cache", "NPEB00874");
        SymbolicLink.ToDirectory(link, elsewhere);
        Declare(install);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([real], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(elsewhere, "entry.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task ACacheFolderThatWillNotBeListedIsReportedAsUnread()
    {
        Dolphin(DolphinRoot);
        using var denied = new DeniedDirectory(Path.Combine(DolphinRoot, "Cache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasUnreadableRoot);
        Assert.True(await provider.IsPresentAsync());
    }

    /// <summary>§5.6: the negative is real, so a root that vanished fails verification loudly.</summary>
    [Fact]
    public async Task VerificationFailsIfTheUsersShadersVanished()
    {
        Dolphin(DolphinRoot);
        var userShaders = Folder(Path.Combine(DolphinRoot, "Shaders"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(userShaders, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(userShaders, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NeverTargetsARootOrACacheFolder()
    {
        Dolphin(DolphinRoot);
        WriteFile(Path.Combine(CemuRoot, "settings.xml"), 64);
        Folder(Path.Combine(CemuRoot, "shaderCache", "precompiled"));

        var plan = await CreateProvider().PlanAsync();

        foreach (var kept in new[] { DolphinRoot, Path.Combine(DolphinRoot, "Cache"), CemuRoot, Path.Combine(CemuRoot, "shaderCache") })
        {
            Assert.DoesNotContain(plan.TargetedPaths, path => IsAtOrUnder(path, kept));
        }
    }
}
