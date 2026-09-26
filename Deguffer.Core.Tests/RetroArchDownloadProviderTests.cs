using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// RetroArch keeps its downloaded sets beside BIOS files, saves, cores and the shader presets the user
/// saved, so these are mostly negative tests: only the three shader sets and the <c>.rdb</c> files are
/// the updater's, and everything else is somebody's.
/// </summary>
public sealed class RetroArchDownloadProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly RetroArchFixture _retroArch;

    public RetroArchDownloadProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        _retroArch = new RetroArchFixture(_environment, Path.Combine(_temp.Path, "RetroArch-Win64"));
    }

    public void Dispose() => _temp.Dispose();

    private RetroArchDownloadProvider CreateProvider(FakeProcessInspector? inspector = null, SteamDiscovery? steam = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, discovery: _retroArch.Discovery(_system, steam));

    /// <summary>
    /// Steam, recorded as it records itself, with RetroArch's manifest in its own library naming
    /// <paramref name="installDir"/>, every backslash doubled as Steam's writer doubles them.
    /// </summary>
    private string RegisterSteam(string installDir)
    {
        var steamRoot = Path.Combine(_temp.Path, "Steam");
        RetroArchFixture.WriteFile(Path.Combine(steamRoot, "steam.exe"), 64);
        _environment.WithRegistryValue(SteamDiscovery.RegistryKey, SteamDiscovery.InstallPathValue, steamRoot.Replace('\\', '/'));
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        File.WriteAllText(
            Path.Combine(steamRoot, "steamapps", $"appmanifest_{RetroArchDiscovery.SteamAppId}.acf"),
            $$"""
            "AppState"
            {
                "appid"		"{{RetroArchDiscovery.SteamAppId}}"
                "name"		"RetroArch"
                "installdir"		"{{installDir.Replace(@"\", @"\\")}}"
            }
            """);

        return steamRoot;
    }

    private static void AssertProtected(CleanupPlan plan, params string[] paths)
    {
        foreach (var path in paths)
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task ReportsNotPresentWhereNoRetroArchIsFound()
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
    /// The shader folder holds the presets the user saved beside the three sets, and the database
    /// folder's parent holds saved queries. Only the updater's own sets go, and §5.6 proves the rest
    /// survived.
    /// </summary>
    [Fact]
    public async Task TakesTheDownloadedSetsAndNothingBesideThem()
    {
        _retroArch.Install().Declare();
        var slang = _retroArch.ShaderSet("shaders_slang");
        var glsl = _retroArch.ShaderSet("shaders_glsl");
        var cg = _retroArch.ShaderSet("shaders_cg");
        var nes = _retroArch.DatabaseFile("Nintendo - Nintendo Entertainment System");
        var snes = _retroArch.DatabaseFile("Nintendo - Super Nintendo Entertainment System");

        var preset = RetroArchFixture.WriteFile(Path.Combine(_retroArch.Shaders, "my-crt.slangp"));
        var presets = RetroArchFixture.Folder(Path.Combine(_retroArch.Shaders, "presets"));
        var notADatabase = RetroArchFixture.WriteFile(Path.Combine(_retroArch.Database, "readme.txt"));
        var cursors = RetroArchFixture.Folder(Path.Combine(_retroArch.Program, "database", "cursors"));
        var bios = RetroArchFixture.WriteFile(Path.Combine(_retroArch.Program, "system", "scph5501.bin"));
        var saves = RetroArchFixture.Folder(Path.Combine(_retroArch.Program, "saves"));
        var playTime = RetroArchFixture.Folder(Path.Combine(_retroArch.Program, "playlists", "logs"));
        var cores = RetroArchFixture.Folder(Path.Combine(_retroArch.Program, "cores"));
        var downloads = RetroArchFixture.Folder(Path.Combine(_retroArch.Program, "downloads"));
        var thumbnails = _retroArch.Pictures("Nintendo - Nintendo Entertainment System", "Named_Boxarts");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { slang, glsl, cg, nes, snes }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        AssertProtected(plan, _retroArch.Program, _retroArch.Shaders, _retroArch.Database, preset, presets,
            notADatabase, cursors, Path.Combine(_retroArch.Program, "system"), saves, playTime, cores, downloads,
            _retroArch.Settings);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(slang));
        Assert.False(File.Exists(nes));

        foreach (var file in new[] { preset, notADatabase, bios, _retroArch.Settings })
        {
            Assert.True(File.Exists(file), file);
        }

        foreach (var folder in new[] { presets, cursors, saves, playTime, cores, downloads, thumbnails, _retroArch.Shaders, _retroArch.Database })
        {
            Assert.True(Directory.Exists(folder), folder);
        }

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A folder shaped like RetroArch's is not a copy of it without the program.</summary>
    [Fact]
    public async Task AFolderWithoutTheProgramIsNotRetroArch()
    {
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(_retroArch.Program).FullName, "retroarch.cfg"), "\n");
        _retroArch.Declare();
        var slang = _retroArch.ShaderSet("shaders_slang");

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).Steps);
        Assert.True(Directory.Exists(slang));
    }

    /// <summary>The folders are settings, so a moved one is followed, and the default one left alone.</summary>
    [Fact]
    public async Task FollowsAShaderFolderTheSettingsMove()
    {
        var moved = Path.Combine(_temp.Path, "retro-shaders");
        _retroArch.Install($"video_shader_dir = \"{moved}\"").Declare();
        var elsewhere = _retroArch.ShaderSet("shaders_slang", moved);
        var stale = _retroArch.ShaderSet("shaders_slang");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([elsewhere], plan.TargetedPaths);
        Assert.True(Directory.Exists(stale));
        AssertProtected(plan, moved);
    }

    /// <summary>
    /// <c>.rdb</c> files are taken by name, so a database folder pointed at a folder of the user's own
    /// keeps everything else in it.
    /// </summary>
    [Fact]
    public async Task TakesOnlyDatabasesFromADatabaseFolderTheUserChose()
    {
        var chosen = Path.Combine(_temp.Path, "games");
        _retroArch.Install($"content_database_path = \"{chosen}\"").Declare();
        var database = _retroArch.DatabaseFile("Sega - Mega Drive - Genesis", chosen);
        var game = RetroArchFixture.WriteFile(Path.Combine(chosen, "Sonic.md"));
        var folder = RetroArchFixture.Folder(Path.Combine(chosen, "more.rdb"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([database], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(game));
        Assert.True(Directory.Exists(folder));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>With no settings beside the program, RetroArch reads <c>%APPDATA%\retroarch.cfg</c>.</summary>
    [Fact]
    public async Task ReadsTheApplicationDataSettingsWhereNoneAreBesideTheProgram()
    {
        var moved = Path.Combine(_temp.Path, "retro-shaders");
        _retroArch.InstallProgramOnly().Declare();
        _retroArch.WriteApplicationDataSettings($"video_shader_dir = \"{moved}\"");
        var slang = _retroArch.ShaderSet("shaders_slang", moved);

        Assert.Equal([slang], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>A settings file beside the program wins whenever it exists, empty or not.</summary>
    [Fact]
    public async Task AnEmptySettingsFileBesideTheProgramStillWins()
    {
        _retroArch.Install().Declare();
        _retroArch.WriteApplicationDataSettings($"video_shader_dir = \"{Path.Combine(_temp.Path, "elsewhere")}\"");
        var slang = _retroArch.ShaderSet("shaders_slang");

        Assert.Equal([slang], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// Settings in <c>%APPDATA%</c> that no program found is reading: their full paths are followed,
    /// and a folder beside an unknown program is said to need the folder declared.
    /// </summary>
    [Fact]
    public async Task SettingsWithNoKnownProgramFollowOnlyFullPaths()
    {
        var moved = Path.Combine(_temp.Path, "retro-shaders");
        _retroArch.WriteApplicationDataSettings(
            $"video_shader_dir = \"{moved}\"",
            "content_database_path = \":\\database\\rdb\"");
        var slang = _retroArch.ShaderSet("shaders_slang", moved);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([slang], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Emulator folders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindsTheCopySteamInstalled()
    {
        var steamRoot = RegisterSteam("RetroArch");

        var steamCopy = new RetroArchFixture(_environment, Path.Combine(steamRoot, "steamapps", "common", "RetroArch")).Install();
        var slang = steamCopy.ShaderSet("shaders_slang");

        var plan = await CreateProvider(steam: new SteamDiscovery(_environment)).PlanAsync();

        Assert.Equal([slang], plan.TargetedPaths);
    }

    /// <summary>An install folder Steam's manifest names is one folder name, never a way out of the library.</summary>
    [Fact]
    public async Task AManifestInstallFolderThatLeavesTheLibraryIsIgnored()
    {
        RegisterSteam(@"..\..\..\RetroArch-Win64");
        _retroArch.Install();
        var slang = _retroArch.ShaderSet("shaders_slang");

        var plan = await CreateProvider(steam: new SteamDiscovery(_environment)).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(Directory.Exists(slang));
    }

    /// <summary>§5.3: RetroArch extracts an update in place while it runs.</summary>
    [Fact]
    public async Task NothingIsOfferedWhileRetroArchRuns()
    {
        _retroArch.Install().Declare();
        var slang = _retroArch.ShaderSet("shaders_slang");

        var provider = CreateProvider(new FakeProcessInspector("retroarch"));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("running", StringComparison.Ordinal));

        var roots = await provider.DiscoverToolRootsAsync();
        Assert.False(Assert.Single(roots, r => r.Path.Equals(_retroArch.Shaders, StringComparison.OrdinalIgnoreCase))
            .Recognises("shaders_slang"));

        await provider.ExecuteAsync(plan);
        Assert.True(Directory.Exists(slang));
    }

    [Fact]
    public async Task ARetroArchStartedAfterThePreviewHoldsItsSetsBack()
    {
        _retroArch.Install().Declare();
        var slang = _retroArch.ShaderSet("shaders_slang");

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.Equal([slang], plan.TargetedPaths);

        inspector.WithRunning("retroarch_angle");
        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(slang), "a shader set was removed under a RetroArch started after the preview");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2 at every level: the program's folder recognises only the way down, and each folder read only
    /// what the plan recognised in it.
    /// </summary>
    [Fact]
    public async Task ExploreIsToldTheWayToTheSetsAndNothingBesideThem()
    {
        _retroArch.Install().Declare();
        _retroArch.ShaderSet("shaders_slang");
        _retroArch.DatabaseFile("Atari - 2600");
        RetroArchFixture.WriteFile(Path.Combine(_retroArch.Shaders, "my-crt.slangp"));

        var roots = await CreateProvider().DiscoverToolRootsAsync();

        var program = roots.Where(r => r.Path.Equals(_retroArch.Program, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(program, r => r.Recognises("shaders"));
        Assert.Contains(program, r => r.Recognises("database"));
        Assert.DoesNotContain(program, r => r.Recognises("saves") || r.Recognises("system") || r.Recognises("retroarch.cfg"));

        var shaders = roots.Where(r => r.Path.Equals(_retroArch.Shaders, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(shaders, r => r.Recognises("shaders_slang"));
        Assert.DoesNotContain(shaders, r => r.Recognises("my-crt.slangp") || r.Recognises("shaders_glsl"));

        var database = roots.Where(r => r.Path.Equals(Path.Combine(_retroArch.Program, "database"), StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(database, r => r.Recognises("rdb"));
        Assert.DoesNotContain(database, r => r.Recognises("cursors"));
    }

    [Fact]
    public async Task AShaderSetThatIsALinkIsNamedAndNotDeletedThrough()
    {
        _retroArch.Install().Declare();
        var real = _retroArch.ShaderSet("shaders_glsl");
        var elsewhere = RetroArchFixture.Folder(Path.Combine(_temp.Path, "elsewhere"));
        var link = Path.Combine(_retroArch.Shaders, "shaders_slang");
        SymbolicLink.ToDirectory(link, elsewhere);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([real], plan.TargetedPaths);
        AssertProtected(plan, link);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(elsewhere, "entry.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A link between the program's folder and a set puts the set somewhere nothing established.</summary>
    [Fact]
    public async Task AFolderReachedThroughALinkIsLeftAlone()
    {
        _retroArch.Install().Declare();
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        var database = _retroArch.DatabaseFile("Atari - 2600", Path.Combine(elsewhere, "rdb"));
        var link = Path.Combine(_retroArch.Program, "database");
        SymbolicLink.ToDirectory(link, elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(database));
    }

    /// <summary>A setting pointed at the profile does not make the profile RetroArch's.</summary>
    [Fact]
    public async Task AFolderSetOntoTheProfileIsRefused()
    {
        _retroArch.Install($"video_shader_dir = \"{_environment.UserProfile}\"").Declare();
        var slang = _retroArch.ShaderSet("shaders_slang", _environment.UserProfile);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(Directory.Exists(slang));
        Assert.DoesNotContain(await provider.DiscoverToolRootsAsync(), r => r.Path.Equals(_environment.UserProfile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AShaderFolderThatWillNotBeListedIsReportedAsUnread()
    {
        _retroArch.Install().Declare();
        _retroArch.ShaderSet("shaders_slang");
        using var denied = new DeniedDirectory(_retroArch.Shaders);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasUnreadableRoot);
        Assert.True(await provider.IsPresentAsync());
    }

    /// <summary>§5.6: the negative is real, so a preset that vanished fails verification loudly.</summary>
    [Fact]
    public async Task VerificationFailsIfASavedPresetVanished()
    {
        _retroArch.Install().Declare();
        _retroArch.ShaderSet("shaders_slang");
        var preset = RetroArchFixture.WriteFile(Path.Combine(_retroArch.Shaders, "my-crt.slangp"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        File.Delete(preset);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(preset, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NeverTargetsTheProgramOrAFolderItReads()
    {
        _retroArch.Install().Declare();
        _retroArch.ShaderSet("shaders_slang");
        _retroArch.DatabaseFile("Atari - 2600");

        var plan = await CreateProvider().PlanAsync();

        foreach (var kept in new[] { _retroArch.Program, _retroArch.Shaders, _retroArch.Database })
        {
            Assert.DoesNotContain(plan.TargetedPaths, path => path.Equals(kept, StringComparison.OrdinalIgnoreCase));
        }
    }
}
