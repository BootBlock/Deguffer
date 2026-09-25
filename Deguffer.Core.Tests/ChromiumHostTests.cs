using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The declared hosts: the browsers, which keep their user-data folder below a vendor directory and
/// a product directory where the one-level walk never looks, and the Battle.net launcher, whose
/// framework marks its folder and each partition with <c>LocalPrefs.json</c> instead. What may go
/// inside a folder is unchanged, so these establish that a declared place is reached, that it is
/// identified by its own marker, and that a link on the way there is never looked through.
/// </summary>
public sealed class ChromiumHostTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ChromiumHostTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private ChromiumCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    private string EdgeUserData => Path.Combine(_environment.LocalAppData, "Microsoft", "Edge", "User Data");

    private static string CreateUserData(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"<REDACTED>\"}}");
        return path;
    }

    private static string CreateDirectory(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[bytes]);
        return path;
    }

    /// <summary>
    /// The layout the issue measured: Edge three levels down, with the user-data-level directories
    /// that are not among the seven sitting beside the profile. Every one of those, the vendor
    /// directory and the browser's own installation directory beside <c>User Data</c> must survive.
    /// </summary>
    [Fact]
    public async Task ReachesANestedBrowserAndLeavesEverythingAroundItsCachesStanding()
    {
        var userData = CreateUserData(EdgeUserData);
        var codeCache = CreateDirectory(Path.Combine(userData, "Default", "Code Cache"));
        var httpCache = CreateDirectory(Path.Combine(userData, "Default", "Cache", "Cache_Data"));
        var gpuCache = CreateDirectory(Path.Combine(userData, "Default", "GPUCache"));

        var localStorage = CreateDirectory(Path.Combine(userData, "Default", "Local Storage"));
        var components = CreateDirectory(Path.Combine(userData, "component_crx_cache"));
        var extensions = CreateDirectory(Path.Combine(userData, "extensions_crx_cache"));
        var shaders = CreateDirectory(Path.Combine(userData, "GrShaderCache"));
        var installation = CreateDirectory(Path.Combine(_environment.LocalAppData, "Microsoft", "Edge", "Application"));
        var vendor = Path.Combine(_environment.LocalAppData, "Microsoft");

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());
        Assert.Equal("Microsoft Edge", Assert.Single(provider.Applications()).Name);

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { codeCache, httpCache, gpuCache }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(
            plan.Steps.OfType<DeleteStep>(),
            step => Assert.Equal("Microsoft Edge — Default", step.Group));

        foreach (var spared in new[] { userData, localStorage, components, extensions, shaders })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(spared, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(codeCache));
        Assert.False(Directory.Exists(httpCache));
        Assert.False(Directory.Exists(gpuCache));

        Assert.All(
            new[] { vendor, installation, userData, localStorage, components, extensions, shaders },
            path => Assert.True(Directory.Exists(path), $"{path} was removed alongside the caches."));
        Assert.True(File.Exists(Path.Combine(userData, "Local State")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The declaration says where to look, never what qualifies. A folder at a declared path that
    /// does not hold <c>Local State</c> is not a browser, however cache-like its contents.
    /// </summary>
    [Fact]
    public async Task ADeclaredPlaceWithoutTheMarkerIsNotABrowser()
    {
        var bystander = CreateDirectory(Path.Combine(EdgeUserData, "Default", "Code Cache"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.Applications());

        var plan = await provider.PlanAsync();
        await provider.ExecuteAsync(plan);

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(bystander));
    }

    /// <summary>
    /// Opera keeps its settings in the roaming tier, with the folder itself as its only profile, so
    /// both the tier and the single-profile layout have to reach it.
    /// </summary>
    [Fact]
    public async Task ReachesABrowserInTheRoamingTierThatIsItsOwnProfile()
    {
        var userData = CreateUserData(Path.Combine(_environment.RoamingAppData, "Opera Software", "Opera Stable"));
        var gpuCache = CreateDirectory(Path.Combine(userData, "GPUCache"));
        var sessions = CreateDirectory(Path.Combine(userData, "Sessions"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal("Opera", Assert.Single(provider.Applications()).Name);
        Assert.Equal(gpuCache, Assert.Single(plan.TargetedPaths), StringComparer.OrdinalIgnoreCase);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(sessions));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A declared path is joined from constants, so nothing enumerated it and nothing filtered its
    /// links out. A vendor directory moved to another drive with a link is the case: without a
    /// check on every segment, the far side is deleted and every §5.6 survivor resolves through the
    /// same link and passes.
    /// </summary>
    [Fact]
    public async Task ALinkOnTheWayToABrowserIsNamedAndNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var farUserData = CreateUserData(Path.Combine(outside, "Edge", "User Data"));
        var bystander = CreateDirectory(Path.Combine(farUserData, "Default", "Code Cache"));

        // A second channel behind the same link, so the link is named once rather than per browser.
        CreateUserData(Path.Combine(outside, "Edge Beta", "User Data"));

        var link = Path.Combine(_environment.LocalAppData, "Microsoft");
        SymbolicLink.ToDirectory(link, outside);

        var provider = CreateProvider();

        Assert.Empty(provider.Applications());
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Single(plan.Notes, n => n.Message.Contains(link, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "discovery looked through a linked vendor directory and deleted the far side.");
    }

    /// <summary>
    /// A link in front of a browser that is not there says nothing. Moving a vendor directory is
    /// ordinary, and naming it for a browser nobody installed would be a sentence about nothing.
    /// </summary>
    [Fact]
    public async Task ALinkInFrontOfNoBrowserIsNotMentioned()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_temp.Path, "elsewhere")).FullName;
        SymbolicLink.ToDirectory(Path.Combine(_environment.LocalAppData, "Microsoft"), outside);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.DoesNotContain(
            (await provider.PlanAsync()).Notes,
            n => n.Message.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A vendor directory Windows will not describe could be a link, and Deguffer cannot tell. So the
    /// browser behind it is not looked inside, and the plan says Deguffer could not reach it rather
    /// than saying either that it is a link or that nothing is there.
    ///
    /// <para>The only refusal a test can build also denies listing the directory above, which is
    /// <c>%LOCALAPPDATA%</c> here, so presence is true on that root's refusal as well. The link test
    /// above is the one that proves an obstructed browser is present on its own.</para>
    /// </summary>
    [Fact]
    public async Task AVendorDirectoryWindowsWillNotDescribeIsSaidSo()
    {
        CreateDirectory(Path.Combine(CreateUserData(EdgeUserData), "Default", "Code Cache"));
        var vendor = Path.Combine(_environment.LocalAppData, "Microsoft");

        using var denied = DeniedDirectory.WithUnreadableAttributes(vendor);

        var provider = CreateProvider();

        Assert.Empty(provider.Applications());
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Single(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(vendor, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.3 names the browser's process, which the folder's own name — <c>User Data</c> — never is.
    /// </summary>
    [Fact]
    public async Task WarnsAboutTheBrowsersOwnProcess()
    {
        CreateDirectory(Path.Combine(CreateUserData(EdgeUserData), "Default", "Code Cache"));

        var plan = await CreateProvider(new FakeProcessInspector("msedge")).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("msedge", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7.1 reads the same declaration from outside, so a browser's caches may be removed from the
    /// Storage page and its credentials may not.
    /// </summary>
    [Theory]
    [InlineData(@"Default\Code Cache", true)]
    [InlineData(@"Default\Cache\Cache_Data", true)]
    [InlineData(@"Default\Local Storage", false)]
    [InlineData(@"Default\Login Data", false)]
    [InlineData("component_crx_cache", false)]
    [InlineData("", false)]
    public void ExploreReadsTheBrowserFromTheSameDeclaration(string relative, bool allowed)
    {
        var userData = CreateUserData(EdgeUserData);
        Directory.CreateDirectory(Path.Combine(userData, "Default"));

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? userData : Path.Combine(userData, relative)).IsAllowed);
    }

    /// <summary>
    /// A row one level down would be found twice, once by the walk and once by the declaration, and
    /// verified twice. A repeated row would be offered twice. And a row in LocalLow, the one tier that
    /// may not resolve, would be a browser that silently disappears.
    /// </summary>
    [Fact]
    public void EveryDeclaredBrowserIsBelowAVendorDirectoryInATierThatAlwaysResolves()
    {
        Assert.All(ChromiumHost.Declared, browser =>
        {
            Assert.True(
                browser.RelativePath.Split(Path.DirectorySeparatorChar).Length >= 2,
                $"{browser.Name} is one level down, where the walk already looks.");
            Assert.NotEqual(ProfileArea.LocalLowAppData, browser.Area);
            Assert.False(string.IsNullOrWhiteSpace(browser.ProcessName));
        });

        Assert.Equal(
            ChromiumHost.Declared.Count,
            ChromiumHost.Declared.Select(b => (b.Area, b.RelativePath.ToUpperInvariant())).Distinct().Count());
        Assert.Equal(
            ChromiumHost.Declared.Count,
            ChromiumHost.Declared.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The Battle.net launcher, as measured: the framework's user-data folder a level below the
    /// launcher's own, marked with <c>LocalPrefs.json</c>, and one partition named <c>common</c>
    /// that no browser's profile rule would recognise. The four caches in the partition go, and
    /// everything around them stays — the launcher's own cache and logs included, which are other
    /// rows' and sit outside the user-data folder.
    /// </summary>
    [Fact]
    public async Task ReachesBattleNetsPartitionByTheFrameworksMarkerAndLeavesEverythingElseStanding()
    {
        var battleNet = new BattleNetFixture(_environment.LocalAppData);
        battleNet.CreateMeasuredLayout();

        var common = battleNet.Common;
        string[] caches =
        [
            Path.Combine(common, "Code Cache"),
            Path.Combine(common, "GPUCache"),
            Path.Combine(common, "DawnCache"),
            Path.Combine(common, "Cache", "Cache_Data"),
        ];

        var provider = CreateProvider();

        var application = Assert.Single(provider.Applications());
        Assert.Equal("Battle.net", application.Name);
        Assert.Equal(ChromiumLayout.EmbeddedFramework, application.Layout);
        Assert.Equal([battleNet.BrowserCaches, common], application.Profiles);

        var plan = await provider.PlanAsync();

        Assert.Equal(
            caches.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan.Steps.OfType<DeleteStep>(), step => Assert.Equal("Battle.net — common", step.Group));

        // Asserted by §5.6, not merely left out: everything inside the user-data folder that is not
        // one of the caches, the two markers included.
        string[] asserted =
        [
            battleNet.BrowserCaches, common, Path.Combine(common, "Cache"),
            Path.Combine(common, "Local Storage"), Path.Combine(common, "Session Storage"),
            Path.Combine(common, "Network"), Path.Combine(common, "Storage"),
            Path.Combine(common, "blob_storage"), Path.Combine(common, "shared_proto_db"),
            Path.Combine(common, "VideoDecodeStats"),
            Path.Combine(battleNet.BrowserCaches, BattleNetFixture.Marker),
            Path.Combine(common, BattleNetFixture.Marker),
        ];

        // The launcher's own folders sit outside the user-data folder, so this row has nothing to
        // say about them. They are other rows' subjects, and are checked on disk below.
        string[] directories =
        [
            battleNet.Launcher, battleNet.BrowserCaches, common, Path.Combine(common, "Cache"),
            Path.Combine(common, "Local Storage"), Path.Combine(common, "Session Storage"),
            Path.Combine(common, "Network"), Path.Combine(common, "Storage"),
            Path.Combine(common, "blob_storage"), Path.Combine(common, "shared_proto_db"),
            Path.Combine(common, "VideoDecodeStats"), battleNet.Cache, battleNet.Logs, battleNet.Account,
        ];
        string[] files =
        [
            Path.Combine(battleNet.BrowserCaches, BattleNetFixture.Marker),
            Path.Combine(common, BattleNetFixture.Marker),
            battleNet.Database,
        ];

        foreach (var spared in asserted)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(spared, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(caches, cache => Assert.False(Directory.Exists(cache), $"{cache} survived."));
        Assert.All(directories, path => Assert.True(Directory.Exists(path), $"{path} was removed alongside the caches."));
        Assert.All(files, path => Assert.True(File.Exists(path), $"{path} was removed alongside the caches."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A plan never holds an extended-length path. The partitions arrive from a listing, which hands
    /// back the prefixed form, so the profile list is where a missing conversion would show.
    /// </summary>
    [Fact]
    public void BattleNetsPartitionsAreHeldInDisplayForm()
    {
        var battleNet = new BattleNetFixture(_environment.LocalAppData);
        BattleNetFixture.Populate(Path.Combine(battleNet.CreatePartition(), "GPUCache"));

        var application = Assert.Single(CreateProvider().Applications());

        Assert.All(application.Profiles, profile => Assert.DoesNotContain(@"\\?\", profile, StringComparison.Ordinal));
        Assert.Contains(battleNet.Common, application.Profiles);
    }

    /// <summary>
    /// A partition is found by holding the framework's marker, never by its name or its contents. A
    /// directory beside <c>common</c> with cache-shaped children and no marker is not looked inside.
    /// </summary>
    [Fact]
    public async Task ABattleNetDirectoryWithoutTheMarkerIsNotAPartition()
    {
        var battleNet = new BattleNetFixture(_environment.LocalAppData);
        BattleNetFixture.Populate(Path.Combine(battleNet.CreatePartition(), "GPUCache"));
        var bystander = BattleNetFixture.Populate(Path.Combine(battleNet.BrowserCaches, "unmarked", "GPUCache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(Path.Combine(battleNet.BrowserCaches, "unmarked"), Assert.Single(provider.Applications()).Profiles);
        Assert.DoesNotContain(bystander, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(bystander), "a directory without the marker was treated as a partition.");
    }

    /// <summary>
    /// The row says where to look, never what qualifies. Without the framework's marker the folder
    /// is not the launcher's browser, and a browser's <c>Local State</c> does not stand in for it:
    /// the marker is the row's own.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Local State")]
    public async Task BattleNetsFolderWithoutItsOwnMarkerIsNotIdentified(string? otherMarker)
    {
        var battleNet = new BattleNetFixture(_environment.LocalAppData);
        var bystander = BattleNetFixture.Populate(Path.Combine(battleNet.BrowserCaches, "GPUCache"));

        if (otherMarker is not null)
        {
            File.WriteAllText(Path.Combine(battleNet.BrowserCaches, otherMarker), "{}");
        }

        var provider = CreateProvider();

        Assert.Empty(provider.Applications());

        var plan = await provider.PlanAsync();
        await provider.ExecuteAsync(plan);

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(bystander));
    }

    /// <summary>
    /// The framework's marker identifies a folder only where a row declares it. One level under an
    /// application-data root, where the walk looks, <c>LocalPrefs.json</c> is a name any application
    /// might give its own settings.
    /// </summary>
    [Fact]
    public async Task TheFrameworksMarkerIsNotEnoughOneLevelDown()
    {
        var folder = Path.Combine(_environment.LocalAppData, "SomeTool");
        var bystander = BattleNetFixture.Populate(Path.Combine(folder, "GPUCache"));
        File.WriteAllText(Path.Combine(folder, BattleNetFixture.Marker), "{}");

        var provider = CreateProvider();

        Assert.Empty(provider.Applications());

        var plan = await provider.PlanAsync();
        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(bystander));
    }

    /// <summary>
    /// A partition that is a link is never a profile, however well it is marked: the listing sets
    /// links aside before the marker is looked for. Otherwise the far side's caches would be deleted
    /// while every survivor named for the partition resolved through the same link and passed.
    ///
    /// <para>The profile list is asserted as well as the disk. The cache walk declines a linked level
    /// on its own, so a discovery that let the link in would still leave the far side standing, and
    /// only the list shows which of the two rules held.</para>
    /// </summary>
    [Fact]
    public async Task ALinkedBattleNetPartitionIsNeverLookedThrough()
    {
        var battleNet = new BattleNetFixture(_environment.LocalAppData);
        var real = battleNet.CreatePartition("real");
        BattleNetFixture.Populate(Path.Combine(real, "GPUCache"));

        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, BattleNetFixture.Marker), "{}");
        var bystander = BattleNetFixture.Populate(Path.Combine(outside, "GPUCache"));

        SymbolicLink.ToDirectory(battleNet.Common, outside);

        var provider = CreateProvider();

        Assert.Equal([battleNet.BrowserCaches, real], Assert.Single(provider.Applications()).Profiles);

        var plan = await provider.PlanAsync();
        await provider.ExecuteAsync(plan);

        Assert.Equal(Path.Combine(real, "GPUCache"), Assert.Single(plan.TargetedPaths), StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(bystander, "entry.bin")), "a linked partition was deleted through.");
    }

    /// <summary>
    /// The same rule one level up. <c>BrowserCaches</c> is joined from constants, so a junction
    /// there would otherwise put every deletion on the far side. The link is named, and nothing
    /// behind it is touched.
    /// </summary>
    [Fact]
    public async Task ALinkedBattleNetBrowserFolderIsNamedAndNeverLookedThrough()
    {
        var battleNet = new BattleNetFixture(_environment.LocalAppData);
        Directory.CreateDirectory(battleNet.Launcher);

        var outside = Path.Combine(_temp.Path, "elsewhere");
        var partition = Path.Combine(outside, "common");
        Directory.CreateDirectory(partition);
        File.WriteAllText(Path.Combine(outside, BattleNetFixture.Marker), "{}");
        File.WriteAllText(Path.Combine(partition, BattleNetFixture.Marker), "{}");
        var bystander = BattleNetFixture.Populate(Path.Combine(partition, "GPUCache"));

        SymbolicLink.ToDirectory(battleNet.BrowserCaches, outside);

        var provider = CreateProvider();

        Assert.Empty(provider.Applications());
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains(battleNet.BrowserCaches, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(bystander, "entry.bin")), "a linked user-data folder was deleted through.");
    }

    /// <summary>§5.3 names the launcher's process, which the folder's name never was.</summary>
    [Fact]
    public async Task WarnsAboutTheLaunchersOwnProcess()
    {
        var battleNet = new BattleNetFixture(_environment.LocalAppData);
        BattleNetFixture.Populate(Path.Combine(battleNet.CreatePartition(), "GPUCache"));

        var plan = await CreateProvider(new FakeProcessInspector("Battle.net")).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("Battle.net", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7.1 from outside: the partition's caches may be removed from the Storage page, and its
    /// sign-in and settings may not, whichever row's declaration covers the path.
    /// </summary>
    [Theory]
    [InlineData(@"BrowserCaches\common\Code Cache", true)]
    [InlineData(@"BrowserCaches\common\DawnCache", true)]
    [InlineData(@"BrowserCaches\common\Cache\Cache_Data", true)]
    [InlineData(@"BrowserCaches\common\Local Storage", false)]
    [InlineData(@"BrowserCaches\common\Network", false)]
    [InlineData(@"BrowserCaches\common", false)]
    [InlineData("BrowserCaches", false)]
    [InlineData("Account", false)]
    [InlineData("", false)]
    public void ExploreReadsBattleNetFromTheDeclarations(string relative, bool allowed)
    {
        var battleNet = new BattleNetFixture(_environment.LocalAppData);
        battleNet.CreateMeasuredLayout();

        var policy = new ExploreActionPolicy(
            [],
            [.. CreateProvider().ToolRoots, .. new BattleNetCacheProvider(_environment).ToolRoots],
            new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? battleNet.Launcher : Path.Combine(battleNet.Launcher, relative)).IsAllowed);
    }
}
