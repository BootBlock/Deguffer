using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A packaged desktop application does not write to <c>%APPDATA%</c> or <c>%LOCALAPPDATA%</c>: Windows
/// keeps what it creates there in <c>Packages\&lt;package&gt;\LocalCache\Roaming</c> and
/// <c>LocalCache\Local</c>. An Electron application installed that way keeps its user-data folder one
/// level into either, and these establish that it is found there by the same marker as anywhere else,
/// and that <c>LocalCache</c> is never on its own a reason to remove anything.
/// </summary>
public sealed class ChromiumPackagedApplicationTests : IDisposable
{
    private const string Package = "Vendor.Chat_0123456789abc";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ChromiumPackagedApplicationTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private ChromiumCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, liveTrees: FakeLiveTreeInspector.NothingLive);

    private string PackageFolder => Path.Combine(_environment.LocalAppData, "Packages", Package);

    private static string CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[4096]);
        return path;
    }

    private static string CreateFile(string path, string contents = "secret")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>
    /// The layout measured for a packaged Electron application: the marker and the caches directly in
    /// its folder, with its sign-in state, a virtual machine's files and its settings beside them. The
    /// caches go, and every neighbour, the folder, <c>LocalCache</c> and the package are asserted to
    /// survive (§5.6).
    /// </summary>
    [Theory]
    [InlineData("Roaming")]
    [InlineData("Local")]
    public async Task FindsAPackagedApplicationOneLevelIntoEachRedirectedRoot(string redirected)
    {
        var cache = Path.Combine(PackageFolder, "LocalCache");
        var userData = Path.Combine(cache, redirected, "Chat");
        var marker = CreateFile(Path.Combine(userData, "Local State"), "{}");
        var codeCache = CreateDirectory(Path.Combine(userData, "Code Cache"));
        var webCache = CreateDirectory(Path.Combine(userData, "Cache", "Cache_Data"));
        var storage = CreateDirectory(Path.Combine(userData, "Local Storage"));
        var machine = CreateDirectory(Path.Combine(userData, "vm_bundles"));
        var settings = CreateFile(Path.Combine(userData, "config.json"));

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var application = Assert.Single(provider.Applications());
        Assert.Equal(userData, application.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Chat (packaged)", application.Name);
        Assert.Equal("Chat", application.ProcessName);
        Assert.Equal(ChromiumLayout.Browser, application.Layout);

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { codeCache, webCache }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var survivor in new[] { userData, marker, storage, machine })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(codeCache));
        Assert.False(Directory.Exists(webCache));

        foreach (var survivor in new[] { storage, machine, Path.Combine(userData, "Cache"), cache, PackageFolder })
        {
            Assert.True(Directory.Exists(survivor), $"{survivor} was removed.");
        }

        Assert.True(File.Exists(marker));
        Assert.True(File.Exists(settings));
    }

    /// <summary>
    /// <c>LocalCache</c> is a name, not a licence. Cache-named directories in it, in a redirected root
    /// itself and in <c>LocalLow</c> beside the two Microsoft names, are left alone, each with a marker
    /// that would identify it anywhere the rule looks.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Roaming")]
    [InlineData(@"LocalLow\Chat")]
    [InlineData(@"Roaming\Vendor\Chat")]
    public async Task NothingInLocalCacheIsRemovedForBeingThere(string below)
    {
        var folder = Path.Combine(PackageFolder, "LocalCache", below);
        CreateFile(Path.Combine(folder, "Local State"), "{}");
        var bystander = CreateDirectory(Path.Combine(folder, "GPUCache"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.Applications());

        var plan = await provider.PlanAsync();
        Assert.Empty(plan.TargetedPaths);

        await provider.ExecuteAsync(plan);
        Assert.True(Directory.Exists(bystander));
    }

    /// <summary>
    /// Each segment of the shape is asked for: a package's other folders are not redirected roots, and
    /// a folder of the same shape outside <c>Packages</c> is not a package.
    /// </summary>
    [Theory]
    [InlineData(@"Packages\Vendor.Chat_0123456789abc\LocalState\Roaming\Chat")]
    [InlineData(@"Vendor\Vendor.Chat_0123456789abc\LocalCache\Roaming\Chat")]
    public async Task TheShapeIdentifiesOnlyInAPackagesLocalCache(string folder)
    {
        var userData = Path.Combine(_environment.LocalAppData, folder);
        CreateFile(Path.Combine(userData, "Local State"), "{}");
        var bystander = CreateDirectory(Path.Combine(userData, "Code Cache"));

        var provider = CreateProvider();

        Assert.Empty(provider.Applications());
        Assert.DoesNotContain(bystander, (await provider.PlanAsync()).TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Only the local root holds the packages. The same shape under <c>%APPDATA%</c> is somebody's
    /// folder of that name, and a marker five levels into it identifies nothing.
    /// </summary>
    [Fact]
    public async Task TheSameShapeUnderTheRoamingRootIsNotAPackage()
    {
        var folder = Path.Combine(_environment.RoamingAppData, "Packages", Package, "LocalCache", "Roaming", "Chat");
        CreateFile(Path.Combine(folder, "Local State"), "{}");
        var bystander = CreateDirectory(Path.Combine(folder, "Code Cache"));

        var provider = CreateProvider();

        Assert.Empty(provider.Applications());
        Assert.DoesNotContain(bystander, (await provider.PlanAsync()).TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A packaged folder is a Chromium user-data folder like any other, so it is a boundary: a WebView2
    /// folder inside it sits in a child the folder's own rules left alone, and is not looked for.
    /// </summary>
    [Fact]
    public async Task NeverFindsAWebView2FolderInsideAPackagedApplicationsFolder()
    {
        var userData = Path.Combine(PackageFolder, "LocalCache", "Roaming", "Chat");
        CreateFile(Path.Combine(userData, "Local State"), "{}");
        CreateDirectory(Path.Combine(userData, "GPUCache"));
        var nested = Path.Combine(userData, "Sign-in", "EBWebView");
        CreateFile(Path.Combine(nested, "Local State"), "{}");
        var bystander = CreateDirectory(Path.Combine(nested, "Default", "Code Cache"));

        var provider = CreateProvider();

        Assert.Equal(userData, Assert.Single(provider.Applications()).Path, StringComparer.OrdinalIgnoreCase);

        var plan = await provider.PlanAsync();
        Assert.DoesNotContain(plan.TargetedPaths, p => p.StartsWith(nested, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);
        Assert.True(Directory.Exists(bystander));
    }

    /// <summary>
    /// The same application installed both ways keeps two folders, and the packaged one is named so
    /// that the two rows of the preview can be told apart.
    /// </summary>
    [Fact]
    public void ThePackagedAndUnpackagedEditionsAreNamedApart()
    {
        foreach (var userData in new[] { Path.Combine(_environment.RoamingAppData, "Chat"), Path.Combine(PackageFolder, "LocalCache", "Roaming", "Chat") })
        {
            CreateFile(Path.Combine(userData, "Local State"), "{}");
            CreateDirectory(Path.Combine(userData, "GPUCache"));
        }

        Assert.Equal(
            ["Chat", "Chat (packaged)"],
            CreateProvider().Applications().Select(a => a.Name).Order(StringComparer.Ordinal));
    }
}
