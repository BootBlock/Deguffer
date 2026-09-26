using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// An application embedding the engine through WebView2 keeps the same user-data folder as any other
/// Chromium host, inside an <c>EBWebView</c> directory it puts wherever it likes: two to six levels
/// below an application-data root on the measured machine. These establish that such a folder is
/// found at any of those depths, that it still has to prove itself, that the walk that finds it does
/// not go where it must not, and that a profile WebView2 names for itself is looked inside with every
/// protection a <c>Default</c> gets.
/// </summary>
public sealed class ChromiumWebView2Tests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ChromiumWebView2Tests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private ChromiumCacheProvider CreateProvider(
        FakeLiveTreeInspector? liveTrees = null,
        FakeProcessInspector? inspector = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive);

    /// <summary>An <c>EBWebView</c> folder holding the marker, below <paramref name="host"/>.</summary>
    private static string CreateWebView2(string host)
    {
        var userData = Path.Combine(host, "EBWebView");
        Directory.CreateDirectory(userData);
        File.WriteAllText(Path.Combine(userData, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"<REDACTED>\"}}");
        return userData;
    }

    private static string CreateDirectory(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[bytes]);
        return path;
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "secret");
        return path;
    }

    /// <summary>
    /// The shallowest and the deepest layouts measured, and the ones between: a vendor's own folder,
    /// a vendor and a product, a packaged application's <c>LocalState</c>, a numbered slot, and a
    /// packaged application's <c>LocalCache</c>. The one-level walk reached none of them.
    /// </summary>
    [Theory]
    [InlineData(@"Vendor")]
    [InlineData(@"Vendor\Product")]
    [InlineData(@"Packages\Vendor.Product_0123456789abc\LocalState")]
    [InlineData(@"Vendor\Product\WebView2Cache\0001")]
    [InlineData(@"Packages\Vendor.Product_0123456789abc\LocalCache\Vendor\Product")]
    public async Task FindsAWebView2FolderAtEveryMeasuredDepth(string host)
    {
        var userData = CreateWebView2(Path.Combine(_environment.LocalAppData, host));
        var cache = CreateDirectory(Path.Combine(userData, "Default", "Cache", "Cache_Data"));
        var shaders = CreateDirectory(Path.Combine(userData, "GrShaderCache"));

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var application = Assert.Single(provider.Applications());
        Assert.Equal(host, application.Name);
        Assert.Equal(ChromiumLayout.WebView2, application.Layout);

        var plan = await provider.PlanAsync();

        Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(shaders, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The roaming root is walked the same way, and the process a WebView2 host's engine runs as is
    /// the one §5.3's warning names.
    /// </summary>
    [Fact]
    public async Task FindsOneInTheRoamingRootAndWarnsAboutTheRuntimesProcess()
    {
        var userData = CreateWebView2(Path.Combine(_environment.RoamingAppData, "Vendor", "webview-data"));
        CreateDirectory(Path.Combine(userData, "Default", "Code Cache"));

        var provider = CreateProvider(inspector: FakeProcessInspector.NothingRunning.WithRunning("msedgewebview2"));
        var application = Assert.Single(provider.Applications());

        Assert.Equal(ChromiumUserDataDiscovery.WebView2ProcessName, application.ProcessName);

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("msedgewebview2 is running", StringComparison.Ordinal));
    }

    /// <summary>
    /// The bound is a bound. A folder one level deeper than any measured is not looked for, which
    /// reclaims nothing rather than deleting anything.
    /// </summary>
    [Fact]
    public async Task DoesNotLookBelowTheDepthBound()
    {
        var host = Path.Combine(_environment.LocalAppData, "a", "b", "c", "d", "e", "f");
        var userData = CreateWebView2(host);
        var bystander = CreateDirectory(Path.Combine(userData, "Default", "Code Cache"));

        Assert.Equal(ChromiumUserDataWalk.WebView2Depth + 1, Path.GetRelativePath(_environment.LocalAppData, userData).Split('\\').Length);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.DoesNotContain(bystander, (await provider.PlanAsync()).TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The name says where to look, never what qualifies. An <c>EBWebView</c> without the marker is
    /// somebody's directory of that name, and a <c>GPUCache</c> inside it is not a licence.
    /// </summary>
    [Fact]
    public async Task AnEBWebViewWithoutLocalStateIsNeverLookedInside()
    {
        var impostor = Path.Combine(_environment.LocalAppData, "Vendor", "EBWebView");
        var bystander = CreateDirectory(Path.Combine(impostor, "GPUCache"));
        CreateDirectory(Path.Combine(impostor, "Default", "Code Cache"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.Empty(plan.TargetedPaths);

        await provider.ExecuteAsync(plan);
        Assert.True(Directory.Exists(bystander));
    }

    /// <summary>
    /// A deeper directory holding <c>Local State</c> that is not called <c>EBWebView</c> is not
    /// identified. The walk exists to find WebView2, and a marker at any depth would identify folders
    /// nobody declared: the packaged Electron application here among them.
    /// </summary>
    [Fact]
    public async Task ADeeperFolderWithTheMarkerButNotTheNameIsNotIdentified()
    {
        var folder = Path.Combine(_environment.LocalAppData, "Packages", "Vendor.Chat_0123456789abc", "LocalCache", "Roaming", "Chat");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Local State"), "{}");
        var bystander = CreateDirectory(Path.Combine(folder, "Code Cache"));

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(bystander, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// §5.2. A Chromium user-data folder is classified as a whole, so a WebView2 folder inside one is
    /// inside a child that folder's rules left alone. Edge keeps one for its sign-in in exactly that
    /// place, and the walk does not go in to find it. Edge's folder is declared as well, so the
    /// second case is a folder nothing declares, which only its <c>Local State</c> stops the walk at.
    /// </summary>
    [Theory]
    [InlineData(@"Microsoft\Edge\User Data")]
    [InlineData(@"Vendor\Chat\User Data")]
    public async Task NeverFindsAWebView2FolderInsideAnotherUserDataFolder(string outer)
    {
        var edge = Path.Combine(_environment.LocalAppData, outer);
        Directory.CreateDirectory(edge);
        File.WriteAllText(Path.Combine(edge, "Local State"), "{}");

        var nested = CreateWebView2(Path.Combine(edge, "OneAuth", "WebView2"));
        var bystander = CreateDirectory(Path.Combine(nested, "Default", "Code Cache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(provider.Applications(), a => a.Path.Equals(nested, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.TargetedPaths, p => p.StartsWith(nested, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);
        Assert.True(Directory.Exists(bystander));
    }

    /// <summary>
    /// The same boundary at a declared host whose folder another file marks. Battle.net's folder holds
    /// <c>LocalPrefs.json</c>, not <c>Local State</c>, and a directory in it that is not a partition is
    /// Tier 4 there, so a WebView2 folder inside that directory is not found either.
    /// </summary>
    [Fact]
    public async Task NeverFindsAWebView2FolderInsideADeclaredHostsFolder()
    {
        var browserCaches = Path.Combine(_environment.LocalAppData, "Battle.net", "BrowserCaches");
        Directory.CreateDirectory(browserCaches);
        File.WriteAllText(Path.Combine(browserCaches, "LocalPrefs.json"), "{}");

        var nested = CreateWebView2(Path.Combine(browserCaches, "Unrecognised"));
        var bystander = CreateDirectory(Path.Combine(nested, "Default", "Code Cache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(provider.Applications(), a => a.Path.Equals(nested, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.TargetedPaths, p => p.StartsWith(nested, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);
        Assert.True(Directory.Exists(bystander));
    }

    /// <summary>
    /// A user-data folder that will not be listed is still identified by its marker, and the plan
    /// says its profiles may be incomplete, as before the walk went deeper.
    /// </summary>
    [Fact]
    public void AFolderThatRefusesTheListingIsStillIdentifiedByItsMarker()
    {
        var userData = CreateWebView2(Path.Combine(_environment.LocalAppData, "Vendor"));
        CreateDirectory(Path.Combine(userData, "GPUCache"));

        using var denied = new DeniedDirectory(userData);

        var application = Assert.Single(new ChromiumUserDataDiscovery(_environment).Discover());

        Assert.Equal(userData, application.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(application.ProfilesIncomplete);
    }

    /// <summary>
    /// The temporary folder is the temporary files row's, and it is where most of a real
    /// <c>%LOCALAPPDATA%</c>'s directories are. A WebView2 folder there is not found by this walk.
    /// </summary>
    [Fact]
    public async Task NeverEntersTheTemporaryFolder()
    {
        var temporary = Path.Combine(_environment.LocalAppData, "Temp");
        _environment.WithTempPath(temporary);

        var userData = CreateWebView2(Path.Combine(temporary, "Vendor"));
        var bystander = CreateDirectory(Path.Combine(userData, "Default", "Code Cache"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.DoesNotContain(bystander, (await provider.PlanAsync()).TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A link anywhere on the way down is not followed, so a WebView2 folder on its far side is never
    /// identified and nothing there is deleted.
    /// </summary>
    [Fact]
    public async Task NeverLooksThroughALinkOnTheWayDown()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var userData = CreateWebView2(Path.Combine(outside, "Product"));
        var bystander = CreateDirectory(Path.Combine(userData, "Default", "Code Cache"));

        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Vendor"));
        SymbolicLink.ToDirectory(Path.Combine(_environment.LocalAppData, "Vendor", "Linked"), outside);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.Empty(plan.TargetedPaths);

        await provider.ExecuteAsync(plan);
        Assert.True(File.Exists(Path.Combine(bystander, "entry.bin")), "the walk followed a link and deleted the far side.");
    }

    /// <summary>
    /// The measured host that uses WebView2's profiles keeps all its real cache, and all its sign-in
    /// state, in a <c>WV2Profile_</c> directory. Its caches are planned, and every credential file in
    /// it is asserted to survive (§5.6), as in a <c>Default</c>.
    /// </summary>
    [Fact]
    public async Task ANamedWebView2ProfileIsPlannedAndItsCredentialsAreProtected()
    {
        var userData = CreateWebView2(Path.Combine(_environment.LocalAppData, "Vendor", "Chat"));
        var profile = Path.Combine(userData, "WV2Profile_tfw");
        var cache = CreateDirectory(Path.Combine(profile, "Cache", "Cache_Data"));
        var code = CreateDirectory(Path.Combine(profile, "Code Cache"));

        var cookies = CreateFile(Path.Combine(profile, "Network", "Cookies"));
        var logins = CreateFile(Path.Combine(profile, "Login Data"));
        var cards = CreateFile(Path.Combine(profile, "Web Data"));
        var storage = CreateDirectory(Path.Combine(profile, "Local Storage"));
        var indexed = CreateDirectory(Path.Combine(profile, "IndexedDB"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(code, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(@"Vendor\Chat — WV2Profile_tfw", plan.Steps.OfType<DeleteStep>().First(s => s.Path.Equals(code, StringComparison.OrdinalIgnoreCase)).Group);

        var protectedPaths = plan.ProtectedPaths.Select(p => p.Path).ToList();

        foreach (var kept in new[] { profile, cookies, logins, cards, storage, indexed })
        {
            Assert.Contains(kept, protectedPaths, StringComparer.OrdinalIgnoreCase);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(code));
        Assert.True(File.Exists(cookies));
        Assert.True(File.Exists(logins));
        Assert.True(File.Exists(cards));
        Assert.True(Directory.Exists(storage));
        Assert.True(Directory.Exists(indexed));
    }

    /// <summary>
    /// §7.1 reads the same declaration, so Explore may remove a cache in a named profile and nothing
    /// beside it. Without the profile in the declaration, its credentials were in no tool root at all.
    /// </summary>
    [Theory]
    [InlineData(@"WV2Profile_tfw\Code Cache", true)]
    [InlineData(@"WV2Profile_tfw\Cache\Cache_Data", true)]
    [InlineData(@"WV2Profile_tfw\Login Data", false)]
    [InlineData(@"WV2Profile_tfw\Local Storage", false)]
    [InlineData(@"WV2Profile_tfw\Vpn Tokens", false)]
    public void ExploreReadsANamedProfileFromTheSameDeclaration(string relative, bool allowed)
    {
        var userData = CreateWebView2(Path.Combine(_environment.LocalAppData, "Vendor", "Chat"));
        Directory.CreateDirectory(Path.Combine(userData, "WV2Profile_tfw"));

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(Path.Combine(userData, relative)).IsAllowed);
    }

    /// <summary>
    /// Only the shape WebView2 writes is a profile: the prefix and a name in the documented
    /// characters. Anything else is never looked inside, and in a folder that is not WebView2's the
    /// prefix means nothing at all.
    /// </summary>
    [Theory]
    [InlineData("EBWebView", "WV2Profile_")]
    [InlineData("EBWebView", "WV2Profile_tfw backup")]
    [InlineData("EBWebView", "WV2Profile")]
    [InlineData("EBWebView", "Copy of WV2Profile_tfw")]
    [InlineData("Chat", "WV2Profile_tfw")]
    public async Task ADirectoryThatOnlyLooksLikeANamedProfileIsNeverLookedInside(string folder, string name)
    {
        var userData = Path.Combine(_environment.RoamingAppData, folder);
        Directory.CreateDirectory(userData);
        File.WriteAllText(Path.Combine(userData, "Local State"), "{}");
        CreateDirectory(Path.Combine(userData, "GPUCache"));

        var bystander = CreateDirectory(Path.Combine(userData, name, "Code Cache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(bystander, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(bystander));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2 inside a WebView2 folder, as in any other: a name the table does not carry is Tier 4, and
    /// §5.6 asserts it survived. <c>component_crx_cache</c> recurs across WebView2 hosts and stays
    /// unclassified here, as it does for a browser.
    /// </summary>
    [Theory]
    [InlineData("component_crx_cache")]
    [InlineData("extensions_crx_cache")]
    [InlineData("SuperCache")]
    public async Task AnUnrecognisedChildIsTier4AndIsAssertedToSurvive(string name)
    {
        var userData = CreateWebView2(Path.Combine(_environment.LocalAppData, "Vendor"));
        var gpu = CreateDirectory(Path.Combine(userData, "Default", "GPUCache"));
        var spared = CreateDirectory(Path.Combine(userData, name));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(gpu, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(spared, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(spared, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(spared));
        Assert.True(Directory.Exists(userData));
        Assert.True(File.Exists(Path.Combine(userData, "Local State")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.3. The runtime names the folder its engine is using on its command line, and Microsoft
    /// documents that the folder cannot be removed while that engine runs. A folder a running engine
    /// was started with is left alone whole, and the plan says what is using it. Another host beside
    /// it is planned as usual.
    /// </summary>
    [Fact]
    public async Task LeavesAFolderARunningEngineWasStartedWith()
    {
        var live = CreateWebView2(Path.Combine(_environment.LocalAppData, "Vendor", "Busy"));
        var liveCache = CreateDirectory(Path.Combine(live, "Default", "Code Cache"));
        var idle = CreateWebView2(Path.Combine(_environment.LocalAppData, "Vendor", "Idle"));
        var idleCache = CreateDirectory(Path.Combine(idle, "Default", "Code Cache"));

        var liveTrees = FakeLiveTreeInspector.NothingLive.WithProgram("msedgewebview2", arguments: [live]);

        var provider = CreateProvider(liveTrees);
        var plan = await provider.PlanAsync();

        Assert.Equal([idleCache], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(live, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains(@"'Vendor\Busy'", StringComparison.Ordinal)
            && n.Message.Contains("msedgewebview2 was started with it", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(liveCache), "a cache in a folder a running engine was using was deleted.");
        Assert.False(Directory.Exists(idleCache));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A folder whose only cache is held back is still a folder with a cache. The plan must not then
    /// say that no application keeps one, and the row must not read as clear.
    /// </summary>
    [Fact]
    public async Task AFolderHeldBackIsNotReportedAsNoCacheAtAll()
    {
        var live = CreateWebView2(Path.Combine(_environment.LocalAppData, "Vendor"));
        CreateDirectory(Path.Combine(live, "Default", "Code Cache"));

        var plan = await CreateProvider(FakeLiveTreeInspector.NothingLive.WithProgram("msedgewebview2", arguments: [live]))
            .PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("No application on this machine keeps a Chromium cache", StringComparison.Ordinal));
    }

    /// <summary>
    /// A preview can sit on screen while the host application starts. The clean asks again before
    /// each removal, and leaves a cache whose folder an engine has taken up since.
    /// </summary>
    [Fact]
    public async Task TheCleanLeavesACacheWhoseEngineStartedAfterThePreview()
    {
        var userData = CreateWebView2(Path.Combine(_environment.LocalAppData, "Vendor"));
        var cache = CreateDirectory(Path.Combine(userData, "Default", "Code Cache"));

        var liveTrees = FakeLiveTreeInspector.NothingLive;
        var provider = CreateProvider(liveTrees);
        var plan = await provider.PlanAsync();

        Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        liveTrees.WithProgram("msedgewebview2", arguments: [userData]);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(cache), "the clean removed a cache an engine started using after the preview.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A planning pass must see the machine as it is now, so dropping this provider's caches drops
    /// the process table its veto reads as well.
    /// </summary>
    [Fact]
    public void InvalidatingTheCachesDropsTheProcessSnapshot()
    {
        var liveTrees = FakeLiveTreeInspector.NothingLive;

        CreateProvider(liveTrees).InvalidateCaches();

        Assert.Equal(1, liveTrees.InvalidateCount);
    }

    /// <summary>A check that could not run must not look like one that found nothing.</summary>
    [Fact]
    public async Task SaysSoWhenItCouldNotTellWhatIsRunning()
    {
        var userData = CreateWebView2(Path.Combine(_environment.LocalAppData, "Vendor"));
        var cache = CreateDirectory(Path.Combine(userData, "Default", "Code Cache"));

        var plan = await CreateProvider(FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("could not check whether anything is using these", StringComparison.Ordinal));
    }
}
