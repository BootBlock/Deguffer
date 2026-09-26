using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Unreal Engine derived data cache every project shares: the filesystem cache of 5.3 and
/// earlier, and the Zen stores of 5.1 to 5.3 and of 5.4 and later.
///
/// <para>The trap this provider is built against is a folder that exists and holds nothing.
/// <c>%LOCALAPPDATA%\UnrealEngine</c> is on every machine that has run the Epic launcher, and was
/// measured holding only settings, so every test of presence here is a test of content.</para>
/// </summary>
public sealed class UnrealDerivedDataCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public UnrealDerivedDataCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string EngineRoot => Path.Combine(_environment.LocalAppData, "UnrealEngine");

    private string Common => Path.Combine(EngineRoot, "Common");

    private string LegacyCache => Path.Combine(Common, "DerivedDataCache");

    private string CurrentStore => Path.Combine(Common, "Zen", "Data");

    private string EpicRoot => Path.Combine(_system.ProgramData, "Epic");

    private string OlderStore => Path.Combine(EpicRoot, "Zen", "Data");

    private UnrealDerivedDataCacheProvider CreateProvider(IProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>A directory with one file in it, so it measures above zero.</summary>
    private static string Populate(string directory, int bytes = 4096, string name = "0123abcd.udd")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name), new byte[bytes]);
        return directory;
    }

    /// <summary>A Zen store as Zen writes one: its marker at the top, and data below.</summary>
    private static string PopulateStore(string store, int bytes = 4096)
    {
        Populate(Path.Combine(store, "cas"), bytes, "blob.ucas");
        File.WriteAllBytes(Path.Combine(store, UnrealCacheLocations.StoreMarker), new byte[16]);
        return store;
    }

    [Fact]
    public async Task ReportsNotPresentWhenUnrealHasNeverRunHere()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// The measured machine: every engine version's folder holds settings and crash reports, and
    /// <c>Common</c> holds cache folders with nothing in them. None of that is a cache, so the row
    /// must not appear, and a plan asked for anyway must promise nothing.
    /// </summary>
    [Fact]
    public async Task SettingsAndEmptyCacheFoldersAreNotPresence()
    {
        Populate(Path.Combine(EngineRoot, "5.3", "Saved", "Config", "WindowsEditor"), name: "EditorPerProjectUserSettings.ini");
        Populate(Path.Combine(EngineRoot, "5.3", "Saved", "crash-reports"), name: "report.txt");
        Directory.CreateDirectory(Path.Combine(LegacyCache, "Buckets", "Empty"));
        Directory.CreateDirectory(CurrentStore);
        Directory.CreateDirectory(OlderStore);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.Empty(plan.Steps);
        Assert.False(plan.WasNotExamined);
    }

    [Fact]
    public async Task PlansTheFilesystemCacheAndBothDefaultStoresAtTier2()
    {
        Populate(Path.Combine(LegacyCache, "Buckets", "Shaders"), bytes: 1000);
        PopulateStore(CurrentStore, bytes: 2000);
        PopulateStore(OlderStore, bytes: 3000);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { LegacyCache, CurrentStore, OlderStore }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);

        // §3: a Tier 2 row is offered, never ticked for the user.
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
    }

    /// <summary>
    /// §5.6 against everything that sits beside the caches, proved by running the plan. The engine
    /// versions' settings, the Zen servers installed beside each store, Epic's other machine-wide
    /// folders, and a folder in <c>Common</c> nothing recognises (§5.2's unrecognised case) must all
    /// be standing afterwards, and each must be named in the plan rather than merely left alone.
    /// </summary>
    [Fact]
    public async Task EverythingBesideTheCachesSurvivesARunAndIsAsserted()
    {
        Populate(Path.Combine(LegacyCache, "Content"));
        PopulateStore(CurrentStore);
        PopulateStore(OlderStore);

        var settings = Populate(Path.Combine(EngineRoot, "5.4", "Saved", "Config"), name: "Editor.ini");
        var currentServer = Populate(Path.Combine(Common, "Zen", "Install"), name: "zenserver.exe");
        var olderServer = Populate(Path.Combine(EpicRoot, "Zen", "Install"), name: "zenserver.exe");
        var unrecognised = Populate(Path.Combine(Common, "Analytics"), name: "state.json");
        var launcher = Populate(Path.Combine(EpicRoot, "EpicGamesLauncher", "Data", "Manifests"), name: "installed.item");
        var installed = Path.Combine(EpicRoot, "UnrealEngineLauncher", "LauncherInstalled.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllBytes(installed, new byte[64]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var named in new[]
        {
            EngineRoot, Common, Path.Combine(EngineRoot, "5.4"), settings, currentServer, EpicRoot, olderServer,
            Path.Combine(EpicRoot, "EpicGamesLauncher"), installed, unrecognised,
        })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(named, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(LegacyCache));
        Assert.False(Directory.Exists(CurrentStore));
        Assert.False(Directory.Exists(OlderStore));

        Assert.True(File.Exists(Path.Combine(settings, "Editor.ini")));
        Assert.True(File.Exists(Path.Combine(currentServer, "zenserver.exe")));
        Assert.True(File.Exists(Path.Combine(olderServer, "zenserver.exe")));
        Assert.True(File.Exists(Path.Combine(unrecognised, "state.json")), "an unrecognised folder in Common was removed");
        Assert.True(File.Exists(Path.Combine(launcher, "installed.item")));
        Assert.True(File.Exists(installed));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// §5.2 on Explore's route. Unreal's own folder, <c>Common</c> and each <c>Zen</c> folder are
    /// refused, as are an engine version's settings, the server beside a store and anything in
    /// <c>Common</c> nothing recognises. The caches themselves are allowed.
    /// </summary>
    [Fact]
    public async Task ExploreAllowsOnlyTheCachesInsideUnrealsFolders()
    {
        Populate(LegacyCache);
        PopulateStore(CurrentStore);
        var server = Populate(Path.Combine(Common, "Zen", "Install"), name: "zenserver.exe");
        var unrecognised = Populate(Path.Combine(Common, "Analytics"), name: "state.json");
        var version = Populate(Path.Combine(EngineRoot, "5.4", "Saved", "Config"), name: "Editor.ini");

        var policy = await ExplorePolicy(CreateProvider());

        Assert.True(policy.MayRemove(LegacyCache).IsAllowed);
        Assert.True(policy.MayRemove(CurrentStore).IsAllowed);

        Assert.False(policy.MayRemove(EngineRoot).IsAllowed);
        Assert.False(policy.MayRemove(Common).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Common, "Zen")).IsAllowed);
        Assert.False(policy.MayRemove(server).IsAllowed);
        Assert.False(policy.MayRemove(unrecognised).IsAllowed);
        Assert.False(policy.MayRemove(version).IsAllowed);
    }

    /// <summary>
    /// §5.3. A running Zen server is writing its store, and removing a store's data under it is not
    /// provably safe, so every store is held back: named, protected, refused in Explore, and said
    /// out loud. The filesystem cache has no server and is still offered.
    /// </summary>
    [Fact]
    public async Task ARunningZenServerHoldsEveryStoreBack()
    {
        Populate(LegacyCache);
        var current = Path.Combine(PopulateStore(CurrentStore), UnrealCacheLocations.StoreMarker);
        var older = Path.Combine(PopulateStore(OlderStore), UnrealCacheLocations.StoreMarker);

        var provider = CreateProvider(new FakeProcessInspector("zenserver"));
        var plan = await provider.PlanAsync();

        Assert.Equal([LegacyCache], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(CurrentStore, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(OlderStore, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("zenserver is running", StringComparison.Ordinal));

        var declared = await provider.DiscoverToolRootsAsync();
        Assert.Equal(
            new[] { CurrentStore, OlderStore }.Order(StringComparer.OrdinalIgnoreCase),
            declared.Select(root => root.Path).Order(StringComparer.OrdinalIgnoreCase));

        var refusal = (await ExplorePolicy(CreateProvider(new FakeProcessInspector("zenserver")))).MayRemove(CurrentStore);
        Assert.False(refusal.IsAllowed);
        Assert.Contains("Zen server is running", refusal.Reason, StringComparison.Ordinal);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(current), "a store was removed under a running server");
        Assert.True(File.Exists(older), "a store was removed under a running server");
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// The stores were offered because no server was running, and the editor starts one. A server
    /// started while the preview was on screen holds every store back at the clean, as it would have
    /// at the preview, and §5.6 proves each one standing. The filesystem cache has no server, and
    /// still goes.
    /// </summary>
    [Fact]
    public async Task AZenServerStartedAfterThePreviewHoldsEveryStoreBack()
    {
        Populate(LegacyCache);
        var current = Path.Combine(PopulateStore(CurrentStore), UnrealCacheLocations.StoreMarker);
        var older = Path.Combine(PopulateStore(OlderStore), UnrealCacheLocations.StoreMarker);

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { LegacyCache, CurrentStore, OlderStore }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        inspector.WithRunning("zenserver");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(current), "a store was removed under a server started after the preview");
        Assert.True(File.Exists(older), "a store was removed under a server started after the preview");
        Assert.False(Directory.Exists(LegacyCache), "the filesystem cache, which no server holds, was kept");
        Assert.Equal(2, result.Steps.Count(step => step.Message == "Nothing was removed: zenserver is running now."));
        AssertProvedStanding(result, CurrentStore);
        AssertProvedStanding(result, OlderStore);
    }

    /// <summary>
    /// A local cache path inside the filesystem cache puts a store there, so the cache is offered on
    /// the same condition as a store, and a server started before the clean holds it back whole.
    /// </summary>
    [Fact]
    public async Task AFilesystemCacheHoldingAStoreIsHeldBackByAServerStartedAfterThePreview()
    {
        Populate(Path.Combine(LegacyCache, "Buckets"));
        var marker = Path.Combine(PopulateStore(Path.Combine(LegacyCache, "Zen")), UnrealCacheLocations.StoreMarker);
        _environment.WithEnvironmentVariable("UE-LocalDataCachePath", LegacyCache);

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.Equal([LegacyCache], plan.TargetedPaths);

        inspector.WithRunning("zenserver");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(marker), "a store was removed with the cache around it under a server started after the preview");
        Assert.Equal("Nothing was removed: zenserver is running now.", Assert.Single(result.Steps).Message);
        AssertProvedStanding(result, LegacyCache);
    }

    private static void AssertProvedStanding(CleanupResult result, string path)
    {
        var check = Assert.Single(result.Verification!.Checks, c => c.Subject.Equals(path, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(VerificationOutcome.Survived, check.Outcome);
        Assert.True(result.Verification.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A row whose only cache is a store held back offers nothing, and must not read "Already
    /// clear" about a store that is full.
    /// </summary>
    [Fact]
    public async Task ARowWhoseOnlyStoreIsHeldBackIsNotCalledClear()
    {
        PopulateStore(CurrentStore);

        var plan = await CreateProvider(new FakeProcessInspector("zenserver")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }

    [Fact]
    public async Task NothingIsHeldBackOrDeclaredWhileNoServerRuns()
    {
        PopulateStore(CurrentStore);

        var provider = CreateProvider();

        Assert.Empty(await provider.DiscoverToolRootsAsync());
        Assert.Equal([CurrentStore], (await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// §5.3's other half. The editor holds files in the filesystem cache open, which is a warning
    /// rather than a refusal, and only where something is going to be removed.
    /// </summary>
    [Fact]
    public async Task ARunningEditorIsAWarningBesideWhatIsOffered()
    {
        Populate(LegacyCache);

        var plan = await CreateProvider(new FakeProcessInspector("UnrealEditor")).PlanAsync();

        Assert.Equal([LegacyCache], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("UnrealEditor", StringComparison.Ordinal));
    }

    /// <summary>
    /// A local cache path moves Zen to a <c>Zen</c> folder inside it, whether the environment or the
    /// editor's own preference set it. Only that store goes: the chosen folder survives, and so does
    /// the filesystem cache an older engine wrote straight into it, which nothing classifies.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALocalCachePathMovesTheStoreAndOnlyTheStoreGoes(bool setInTheRegistry)
    {
        var chosen = _temp.CreateDirectory("Caches", "Unreal");
        var store = PopulateStore(Path.Combine(chosen, "Zen"));
        var older = Populate(Path.Combine(chosen, "Buckets", "Shaders"));
        var bystander = Populate(Path.Combine(chosen, "Notes"), name: "mine.txt");

        if (setInTheRegistry)
        {
            _environment.WithRegistryValue(@"Software\Epic Games\GlobalDataCachePath", "UE-LocalDataCachePath", chosen);
        }
        else
        {
            _environment.WithEnvironmentVariable("UE-LocalDataCachePath", chosen);
        }

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([store], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(store));
        Assert.True(Directory.Exists(older), "the filesystem cache beside the store was classified");
        Assert.True(File.Exists(Path.Combine(bystander, "mine.txt")));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// Zen's own data path names the store itself, from either environment variable or the
    /// registry.
    /// </summary>
    [Theory]
    [InlineData("UE-ZenDataPath")]
    [InlineData("UE-ZenSubprocessDataPath")]
    [InlineData(null)]
    public async Task ZensOwnDataPathNamesTheStoreItself(string? variable)
    {
        var parent = _temp.CreateDirectory("Stores");
        var store = PopulateStore(Path.Combine(parent, "ZenData"));
        var bystander = Populate(Path.Combine(parent, "Notes"), name: "mine.txt");

        if (variable is null)
        {
            _environment.WithRegistryValue(@"Software\Epic Games\Zen", "DataPath", store);
        }
        else
        {
            _environment.WithEnvironmentVariable(variable, store);
        }

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([store], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(parent, StringComparison.OrdinalIgnoreCase));

        var provider = CreateProvider();
        await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(store));
        Assert.True(File.Exists(Path.Combine(bystander, "mine.txt")), "a folder beside the store was removed");
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// A folder a setting names is somebody's own, so it is a store only where Zen marked it. A
    /// <c>Zen</c> folder with no <c>root_manifest</c> is somebody else's, and is never reached.
    /// </summary>
    [Fact]
    public async Task AFolderASettingNamesIsNotAStoreWithoutZensMarker()
    {
        var chosen = _temp.CreateDirectory("Caches", "Unreal");
        var lookalike = Populate(Path.Combine(chosen, "Zen"), name: "meditation.mp3");
        _environment.WithEnvironmentVariable("UE-LocalDataCachePath", chosen);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        await provider.ExecuteAsync(plan);
        Assert.True(File.Exists(Path.Combine(lookalike, "meditation.mp3")));
    }

    /// <summary>
    /// A local cache path set to <c>Common</c> itself makes <c>Common\Zen</c> look like a store, and
    /// that folder holds the server's installation and the current store. The setting is dropped
    /// rather than followed, and the store is reached by its default name alone.
    /// </summary>
    [Fact]
    public async Task ASettingThatWouldTakeTheServerWithTheStoreIsNotFollowed()
    {
        PopulateStore(CurrentStore);
        var server = Populate(Path.Combine(Common, "Zen", "Install"), name: "zenserver.exe");
        File.WriteAllBytes(Path.Combine(Common, "Zen", UnrealCacheLocations.StoreMarker), new byte[16]);
        _environment.WithEnvironmentVariable("UE-LocalDataCachePath", Common);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([CurrentStore], plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(server, "zenserver.exe")));
    }

    /// <summary>
    /// "None" is how Unreal is told to use no local cache at all, and it names no folder. A relative
    /// value would resolve against Deguffer's own working folder, which nobody pointed at. Asked of
    /// the settings reader, because a relative value's store could only be built in that folder.
    /// </summary>
    [Fact]
    public void ASettingThatIsNotAFullPathNamesNoStore()
    {
        var chosen = _temp.CreateDirectory("Caches");
        _environment.WithEnvironmentVariable("UE-LocalDataCachePath", "None");
        _environment.WithEnvironmentVariable("UE-ZenDataPath", @"Zen\Data");
        _environment.WithRegistryValue(@"Software\Epic Games\Zen", "DataPath", Path.Combine(chosen, "Store"));

        Assert.Equal(
            [Path.Combine(chosen, "Store")],
            UnrealCacheLocations.ConfiguredStores(_environment).Select(store => store.Path));
    }

    /// <summary>
    /// A Zen data path naming a volume's root would make the whole volume the store, so it names
    /// none. A local cache path at a volume's root still names the <c>Zen</c> folder inside it.
    /// </summary>
    [Fact]
    public void AZenDataPathAtAVolumesRootNamesNoStore()
    {
        var volume = Path.GetPathRoot(_temp.Path)!;
        _environment.WithEnvironmentVariable("UE-ZenDataPath", volume);
        _environment.WithEnvironmentVariable("UE-LocalDataCachePath", volume);

        Assert.Equal(
            [Path.Combine(volume, "Zen")],
            UnrealCacheLocations.ConfiguredStores(_environment).Select(store => store.Path));
    }

    /// <summary>
    /// Zen writes into whatever folder it is given, so its marker proves only that it has been
    /// there. A folder a setting names that holds anything Zen does not write is somebody's own, and
    /// neither it nor anything in it is removed (§5.2).
    /// </summary>
    [Fact]
    public async Task ANamedStoreHoldingAnythingNotZensIsLeftAlone()
    {
        var work = PopulateStore(_temp.CreateDirectory("Work"));
        var mine = Path.Combine(work, "report.docx");
        File.WriteAllBytes(mine, new byte[32]);
        _environment.WithEnvironmentVariable("UE-ZenDataPath", work);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(mine));
    }

    /// <summary>
    /// A local cache path set to the filesystem cache itself puts a store inside that cache, so
    /// removing the cache would take the store. While a server runs, the cache is held back with the
    /// store in it, refused in Explore, and both are standing after a run.
    /// </summary>
    [Fact]
    public async Task AStoreInsideTheFilesystemCacheSurvivesARunningServer()
    {
        Populate(Path.Combine(LegacyCache, "Buckets"));
        var nested = PopulateStore(Path.Combine(LegacyCache, "Zen"));
        var marker = Path.Combine(nested, UnrealCacheLocations.StoreMarker);
        _environment.WithEnvironmentVariable("UE-LocalDataCachePath", LegacyCache);

        var provider = CreateProvider(new FakeProcessInspector("zenserver"));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(LegacyCache, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(await provider.DiscoverToolRootsAsync(), root =>
            root.Path.Equals(LegacyCache, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(marker), "a store was removed with the cache around it under a running server");
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// With no server running, the same store is removed with the cache around it rather than as a
    /// step of its own, so no path is both a target and a survivor.
    /// </summary>
    [Fact]
    public async Task AStoreInsideTheFilesystemCacheGoesWithItWhileNoServerRuns()
    {
        Populate(Path.Combine(LegacyCache, "Buckets"));
        PopulateStore(Path.Combine(LegacyCache, "Zen"));
        _environment.WithEnvironmentVariable("UE-LocalDataCachePath", LegacyCache);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([LegacyCache], plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(LegacyCache));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// Two settings naming one folder, one as a local cache path and one as Zen's data path, would
    /// make one store the survivor of the other's removal. Neither is followed.
    /// </summary>
    [Fact]
    public async Task NamedStoresInsideOneAnotherAreNotFollowed()
    {
        var outer = _temp.CreateDirectory("Caches", "Unreal");
        PopulateStore(outer);
        PopulateStore(Path.Combine(outer, "cache", "Zen"));
        _environment.WithEnvironmentVariable("UE-ZenDataPath", outer);
        _environment.WithEnvironmentVariable("UE-LocalDataCachePath", Path.Combine(outer, "cache"));

        Assert.Empty((await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// A cache reached by name has none of the protection an enumeration gives, so a linked one is
    /// named and left, and the far side of the link survives the run.
    /// </summary>
    [Fact]
    public async Task ALinkedCacheIsNamedRatherThanFollowed()
    {
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"), name: "irreplaceable.bin");
        Directory.CreateDirectory(Common);
        SymbolicLink.ToDirectory(LegacyCache, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(outside, "irreplaceable.bin")), "a linked cache was deleted through");
    }

    /// <summary>
    /// A presence probe must never read a refusal as absence: a store Windows would not describe
    /// may be full, and a row that never appears is the one state nothing can correct.
    /// </summary>
    [Fact]
    public async Task AStoreWindowsWillNotDescribeIsPresence()
    {
        PopulateStore(CurrentStore);
        using var denied = DeniedDirectory.WithUnreadableAttributes(CurrentStore);

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    private Task<ExploreActionPolicy> ExplorePolicy(ICleanupProvider provider) =>
        ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);
}
