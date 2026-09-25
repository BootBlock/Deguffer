using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The browsers themselves, which keep their user-data folder below a vendor directory and a
/// product directory where the one-level walk never looks. What qualifies a folder and what may go
/// inside it are unchanged, so these establish only that a declared place is reached, that it is
/// reached on the same terms, and that a link on the way there is never looked through.
/// </summary>
public sealed class ChromiumBrowserTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ChromiumBrowserTests() => _environment = new FakeUserEnvironment(_temp.Path);

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
    /// that are not among the six sitting beside the profile. Every one of those, the vendor
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
        Directory.CreateSymbolicLink(link, outside);

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
        Directory.CreateSymbolicLink(Path.Combine(_environment.LocalAppData, "Microsoft"), outside);

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
        Assert.All(ChromiumBrowser.Declared, browser =>
        {
            Assert.True(
                browser.RelativePath.Split(Path.DirectorySeparatorChar).Length >= 2,
                $"{browser.Name} is one level down, where the walk already looks.");
            Assert.NotEqual(ProfileArea.LocalLowAppData, browser.Area);
            Assert.False(string.IsNullOrWhiteSpace(browser.ProcessName));
        });

        Assert.Equal(
            ChromiumBrowser.Declared.Count,
            ChromiumBrowser.Declared.Select(b => (b.Area, b.RelativePath.ToUpperInvariant())).Distinct().Count());
        Assert.Equal(
            ChromiumBrowser.Declared.Count,
            ChromiumBrowser.Declared.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
