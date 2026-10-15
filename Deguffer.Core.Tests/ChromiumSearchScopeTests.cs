using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Chromium user-data folders are found only where a policy's scope can ask about them, and read
/// from a scan's tree where the scan listed the application-data root. These establish that the tree
/// gives the answer the disk would, that it is the tree and not the disk being read, that a root left
/// out is refused whole rather than left open, and that each scan finds what was created since the
/// last.
/// </summary>
public sealed class ChromiumSearchScopeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeVolumeInventory _volumes = new();

    public ChromiumSearchScopeTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A folder holding the marker that identifies a Chromium user-data folder.</summary>
    private static string CreateUserData(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Local State"), "{}");
        File.WriteAllText(Path.Combine(folder, "Login Data"), "secret");
        Directory.CreateDirectory(Path.Combine(folder, "Default"));
        Directory.CreateDirectory(Path.Combine(folder, "GPUCache"));
        return folder;
    }

    /// <summary>What an Explore scan of <paramref name="root"/> finds, read the way the page reads it.</summary>
    private static ExploreTree Scan(string root) =>
        WalkExploreReader.Read(root, ScanTuner.Shipped, OccupancyProbe.Default, progress: null, TimeProvider.System, default);

    private ProtectionScope Scanned(string root) =>
        ProtectionScope.Scanned(root, Task.FromResult<ExploreScan?>(ExploreScan.Walked(Scan(root), FallbackReason.NotElevated)), _volumes);

    private ChromiumUserDataDiscovery Discovery(ProtectionScope? scope = null) => new(_environment, scope: scope);

    /// <summary>The policy Explore builds over the two Chromium rows, sharing one scoped discovery.</summary>
    private Task<ExploreActionPolicy> PolicyAsync(ProtectionScope scope)
    {
        var discovery = Discovery(scope);

        return ExploreActionPolicy.ForAsync(
            _system,
            _environment,
            _volumes,
            [
                new ChromiumCacheProvider(_environment, discovery: discovery),
                new ChromiumServiceWorkerStorageProvider(_environment, discovery: discovery),
            ]);
    }

    private static IEnumerable<string> Described(IEnumerable<ChromiumUserData> found) =>
        found.Select(application =>
            $"{application.Name}|{application.Path}|{application.Layout.IdentifyingFile}|{string.Join(';', application.Profiles)}");

    /// <summary>
    /// The tree holds every listing a walk would make, so it gives the same folders: one beside the
    /// root, a WebView2 folder four levels down, and not a WebView2 directory without the marker, nor
    /// one inside a declared browser's folder, which is classified whole and never entered.
    /// </summary>
    [Fact]
    public void AScannedRootGivesTheFoldersTheDiskGives()
    {
        CreateUserData(Path.Combine(_environment.LocalAppData, "Vendor"));
        CreateUserData(Path.Combine(_environment.RoamingAppData, "Vendor", "Product", "Cache", "EBWebView"));
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Other", "EBWebView"));
        CreateUserData(Path.Combine(_environment.LocalAppData, "Google", "Chrome", "User Data", "EBWebView"));

        var fromTree = Discovery(Scanned(_temp.Path)).Discover();
        var fromDisk = Discovery().Discover();

        Assert.Equal(2, fromDisk.Count);
        Assert.Equal(Described(fromDisk), Described(fromTree));
    }

    /// <summary>
    /// It is the tree being read and not the disk: a folder created after the scan is not in what the
    /// scan found, and is in what a walk of the disk finds.
    /// </summary>
    [Fact]
    public async Task AScannedRootIsReadFromTheTreeRatherThanListedAgain()
    {
        CreateUserData(Path.Combine(_environment.LocalAppData, "Vendor"));
        var scope = Scanned(_temp.Path);
        var later = CreateUserData(Path.Combine(_environment.LocalAppData, "Later"));

        var discovery = Discovery(scope);

        Assert.DoesNotContain(discovery.Discover(), application => application.Path == later);
        Assert.Contains(Discovery().Discover(), application => application.Path == later);
        Assert.Empty(await discovery.NotSearchedAsync());
    }

    /// <summary>
    /// A file table read record by record can miss a <c>Local State</c> the engine replaced while it
    /// was read, so where the tree could identify a folder and shows no marker, the disk is asked.
    /// Here the marker is written after the scan, which leaves the tree without it as that race does.
    /// </summary>
    [Fact]
    public void AMarkerTheTreeLacksIsAskedOfTheDiskWhereItWouldIdentify()
    {
        var vendor = Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Vendor")).FullName;
        var webView = Directory.CreateDirectory(Path.Combine(_environment.RoamingAppData, "Vendor", "Product", "EBWebView")).FullName;
        var scope = Scanned(_temp.Path);

        File.WriteAllText(Path.Combine(vendor, "Local State"), "{}");
        File.WriteAllText(Path.Combine(webView, "Local State"), "{}");

        Assert.Equal(
            [vendor, webView],
            Discovery(scope).Discover().Select(application => application.Path).Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A tree drawn from a file table that was not read whole may lack any record, so it never stands
    /// in for the disk: the root is walked, and finds what the tree does not hold.
    /// </summary>
    [Fact]
    public async Task AnIncompleteFileTableIsNotReadInPlaceOfTheDisk()
    {
        var scan = ExploreScan.Fast(Scan(_temp.Path), fromIncompleteTable: true);
        var later = CreateUserData(Path.Combine(_environment.LocalAppData, "Later"));

        var discovery = Discovery(ProtectionScope.Scanned(_temp.Path, Task.FromResult<ExploreScan?>(scan), _volumes));

        Assert.Equal(later, Assert.Single(discovery.Discover()).Path);
        Assert.Equal(FolderReach.Reached, (await ProtectionScope.Scanned(_temp.Path, Task.FromResult<ExploreScan?>(scan), _volumes).ReachAsync(_environment.LocalAppData)).Reach);
    }

    /// <summary>
    /// Asked for the folders as they are now, the discovery no longer reads the scan's tree, which says
    /// how they were.
    /// </summary>
    [Fact]
    public void InvalidatingWalksTheDiskRatherThanReadingTheTree()
    {
        var discovery = Discovery(Scanned(_temp.Path));
        Assert.Empty(discovery.Discover());

        var later = CreateUserData(Path.Combine(_environment.LocalAppData, "Later"));
        discovery.Invalidate();

        Assert.Equal(later, Assert.Single(discovery.Discover()).Path);
    }

    /// <summary>
    /// A scope that reaches neither application-data root does not look in either, and the policy
    /// built over it refuses everything in both: a cache it would otherwise recognise, the passwords
    /// beside it, and a file no Chromium folder holds. Nothing in the scope is there, so nothing it
    /// meant to ask about is refused, and anything asked anyway is refused rather than allowed unseen.
    /// </summary>
    [Fact]
    public async Task ARootNothingInTheScopeReachesIsNotSearchedAndIsRefusedWhole()
    {
        var browser = CreateUserData(Path.Combine(_environment.LocalAppData, "Vendor"));
        var unrelated = Path.Combine(_environment.RoamingAppData, "Notes", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "notes");
        var elsewhere = _temp.CreateDirectory("profile", "Music");

        var scope = ProtectionScope.Within([elsewhere], _volumes);
        var discovery = Discovery(scope);

        Assert.Empty(discovery.Discover());
        Assert.Equal([_environment.RoamingAppData, _environment.LocalAppData], await discovery.NotSearchedAsync());

        var policy = await PolicyAsync(scope);

        foreach (var path in new[] { Path.Combine(browser, "GPUCache"), Path.Combine(browser, "Login Data"), unrelated })
        {
            var verdict = policy.MayRemove(path);

            Assert.False(verdict.IsAllowed, path);
            Assert.Contains("did not look in here", verdict.Reason);
        }

        var control = policy.MayRemove(Path.Combine(elsewhere, "track.flac"));
        Assert.True(control.IsAllowed, control.Reason);
    }

    /// <summary>
    /// The policy built for the next scan finds a Chromium folder created since the last one, so its
    /// sign-in data is refused. A discovery kept from one build to the next would miss it.
    /// </summary>
    [Fact]
    public async Task AChromiumFolderCreatedBetweenTwoScansIsStillRefused()
    {
        CreateUserData(Path.Combine(_environment.LocalAppData, "Vendor"));
        var first = await PolicyAsync(Scanned(_temp.Path));
        Assert.False(first.MayRemove(Path.Combine(_environment.LocalAppData, "Vendor", "Login Data")).IsAllowed);

        var created = CreateUserData(Path.Combine(_environment.LocalAppData, "Created"));
        var second = await PolicyAsync(Scanned(_temp.Path));

        Assert.False(second.MayRemove(Path.Combine(created, "Login Data")).IsAllowed);
        Assert.False(second.MayRemove(Path.Combine(created, "Default")).IsAllowed);
        Assert.True(second.MayRemove(Path.Combine(created, "GPUCache")).IsAllowed);
    }

    /// <summary>
    /// A scan of a folder inside an application-data root cannot see a Chromium folder above it, which
    /// holds it, so the root is walked rather than left out or read from the partial tree.
    /// </summary>
    [Fact]
    public async Task AScanInsideAnApplicationDataRootWalksTheRoot()
    {
        var browser = CreateUserData(Path.Combine(_environment.LocalAppData, "Vendor"));
        var inside = Path.Combine(browser, "Default");

        var discovery = Discovery(Scanned(inside));

        Assert.Equal(browser, Assert.Single(discovery.Discover()).Path);
        Assert.Equal([_environment.RoamingAppData], await discovery.NotSearchedAsync());
    }

    /// <summary>
    /// A scan that ends without a tree leaves the page showing whatever it showed before, so the policy
    /// built for it looks in both roots.
    /// </summary>
    [Fact]
    public async Task AScanThatEndsWithoutATreeLooksEverywhere()
    {
        var browser = CreateUserData(Path.Combine(_environment.LocalAppData, "Vendor"));
        var elsewhere = _temp.CreateDirectory("Music");

        var discovery = Discovery(ProtectionScope.Scanned(elsewhere, Task.FromResult<ExploreScan?>(null), _volumes));

        Assert.Equal(browser, Assert.Single(discovery.Discover()).Path);
        Assert.Empty(await discovery.NotSearchedAsync());
    }

    /// <summary>
    /// A scope is compared by where its folders are, not by how they are named: a drive letter
    /// substituted for the profile reaches the roots inside it.
    /// </summary>
    [Fact]
    public async Task AScopeReachesARootThroughASubstitutedDrive()
    {
        var volumes = _volumes.Substituting(@"S:\", _environment.UserProfile);
        var scope = ProtectionScope.Within([@"S:\AppData"], volumes);

        Assert.Equal(FolderReach.Reached, (await scope.ReachAsync(_environment.LocalAppData)).Reach);
        Assert.Equal(FolderReach.Outside, (await scope.ReachAsync(_temp.CreateDirectory("Music"))).Reach);
    }
}
