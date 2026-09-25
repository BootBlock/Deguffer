using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Capture One's previews and thumbnails, found through its own list of catalogs and sessions.
///
/// <para>Three things have to hold. Only a folder called <c>Cache</c> is ever a target, and only in a
/// catalog proved by its database or in a <c>CaptureOne</c> folder of a session proved by its own.
/// Everything beside it survives a run, asserted by name, because beside it sit the user's
/// photographs and every adjustment they made. And a catalog or session Capture One has open is
/// left alone.</para>
/// </summary>
public sealed class CaptureOneCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public CaptureOneCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>Where the catalogs and sessions are, which would be a shoot drive.</summary>
    private string ShootDrive => Path.Combine(_temp.Path, "shoot-drive");

    private CaptureOneCacheProvider CreateProvider(
        FakeLiveTreeInspector? liveTrees = null,
        FakeProcessInspector? inspector = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive);

    private static string Populate(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[4096]);
        return path;
    }

    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[512]);
        return path;
    }

    /// <summary>Capture One's settings, listing <paramref name="documents"/> as recently used.</summary>
    private void List(params string[] documents)
    {
        var folder = Path.Combine(_environment.LocalAppData, "Capture_One", "CaptureOne.exe_StrongName_abc", "16.4.0.0");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, CaptureOneDocuments.SettingsFileName),
            $"""
            <configuration>
                <userSettings>
                    <CaptureOne.Properties.Settings>
                        <setting name="RecentlyUsedDocuments" serializeAs="Xml">
                            <value><ArrayOfString>{string.Concat(documents.Select(d => $"<string>{d}</string>"))}</ArrayOfString></value>
                        </setting>
                    </CaptureOne.Properties.Settings>
                </userSettings>
            </configuration>
            """);
    }

    /// <summary>A catalog laid out as Capture One lays one out on Windows, with a cache in it.</summary>
    private string Catalog(string name)
    {
        var catalog = Path.Combine(ShootDrive, $"{name}.cocatalog");
        WriteFile(Path.Combine(catalog, $"{name}.cocatalogdb"));
        WriteFile(Path.Combine(catalog, "Originals", "2024", "06", "01", "IMG_0001.CR3"));
        WriteFile(Path.Combine(catalog, "Adjustments", "LAM", "mask.comask"));
        Populate(Path.Combine(catalog, "Cache", "Thumbnails"));
        Populate(Path.Combine(catalog, "Cache", "Previews"));
        return catalog;
    }

    /// <summary>A session with a sidecar beside each of its folders of images.</summary>
    private string Session(string name)
    {
        var session = Path.Combine(ShootDrive, name);
        WriteFile(Path.Combine(session, $"{Path.GetFileName(name)}.cosessiondb"));

        foreach (var images in new[] { "Capture", "Selects" })
        {
            WriteFile(Path.Combine(session, images, "IMG_0001.CR3"));
            WriteFile(Path.Combine(session, images, "CaptureOne", "Settings166", "IMG_0001.CR3.cos"));
            Populate(Path.Combine(session, images, "CaptureOne", "Cache", "Proxies"));
        }

        Directory.CreateDirectory(Path.Combine(session, "Output"));
        Directory.CreateDirectory(Path.Combine(session, "Trash"));
        return session;
    }

    private static string CacheOf(string folder) => Path.Combine(folder, "Cache");

    [Fact]
    public async Task ReportsNotPresentWhereCaptureOneHasNeverRun()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
        Assert.Empty(await provider.DiscoverToolRootsAsync());
    }

    [Fact]
    public void IsTierTwoAndSaysWhatOfflineBrowsingLoses()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);
        Assert.Contains("not connected", provider.WhatHappensOnNextUse, StringComparison.Ordinal);
        Assert.Contains("offline", provider.WhatHappensOnNextUse, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.2 and §5.6 for a catalog: the cache is the one target, and the database, the photographs
    /// imported into the catalog and the masks all survive the run, on the disk as in the report.
    /// </summary>
    [Fact]
    public async Task OffersOnlyACatalogsCacheAndEverythingBesideItSurvives()
    {
        var catalog = Catalog("Weddings");
        var stranger = WriteFile(Path.Combine(catalog, "Settings170", "unknown.bin"));
        List(catalog);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([CacheOf(catalog)], plan.Steps.OfType<DeleteStep>().Select(s => s.Path));

        foreach (var survivor in new[]
        {
            catalog,
            Path.Combine(catalog, "Weddings.cocatalogdb"),
            Path.Combine(catalog, "Originals"),
            Path.Combine(catalog, "Adjustments"),
            Path.GetDirectoryName(stranger)!,
        })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase));
        }

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);

        Assert.False(LongPath.DirectoryExists(CacheOf(catalog)));
        Assert.True(LongPath.FileExists(Path.Combine(catalog, "Weddings.cocatalogdb")));
        Assert.True(LongPath.FileExists(Path.Combine(catalog, "Originals", "2024", "06", "01", "IMG_0001.CR3")));
        Assert.True(LongPath.FileExists(Path.Combine(catalog, "Adjustments", "LAM", "mask.comask")));
        Assert.True(LongPath.FileExists(stranger));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// §5.2's unrecognised case: a folder Capture One's list calls a catalog, with no catalog
    /// database in it, is not a catalog. Its <c>Cache</c> is left alone and named.
    /// </summary>
    [Fact]
    public async Task AListedCatalogWithNoDatabaseIsLeftAlone()
    {
        var catalog = Path.Combine(ShootDrive, "Renamed.cocatalog");
        var cache = Populate(CacheOf(catalog));
        List(catalog);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(catalog, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains(catalog, StringComparison.Ordinal));
        Assert.True(LongPath.DirectoryExists(cache));
    }

    /// <summary>
    /// §5.2 and §5.6 for a session: the cache in each sidecar is a target, and the adjustments beside
    /// it, the images, the sidecar and the session file all survive.
    /// </summary>
    [Fact]
    public async Task OffersEachSidecarsCacheInASessionAndTheEditsSurvive()
    {
        var session = Session("2024-06-01");
        List(Path.Combine(session, "2024-06-01.cosessiondb"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            [
                CacheOf(Path.Combine(session, "Capture", "CaptureOne")),
                CacheOf(Path.Combine(session, "Selects", "CaptureOne")),
            ],
            plan.Steps.OfType<DeleteStep>().Select(s => s.Path).Order(StringComparer.OrdinalIgnoreCase));

        foreach (var survivor in new[]
        {
            session,
            Path.Combine(session, "2024-06-01.cosessiondb"),
            Path.Combine(session, "Capture"),
            Path.Combine(session, "Capture", "CaptureOne"),
            Path.Combine(session, "Capture", "CaptureOne", "Settings166"),
            Path.Combine(session, "Selects"),
            Path.Combine(session, "Selects", "CaptureOne"),
            Path.Combine(session, "Selects", "CaptureOne", "Settings166"),
        })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase));
        }

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);

        foreach (var images in new[] { "Capture", "Selects" })
        {
            Assert.False(LongPath.DirectoryExists(CacheOf(Path.Combine(session, images, "CaptureOne"))));
            Assert.True(LongPath.FileExists(Path.Combine(session, images, "IMG_0001.CR3")));
            Assert.True(LongPath.FileExists(Path.Combine(session, images, "CaptureOne", "Settings166", "IMG_0001.CR3.cos")));
        }

        Assert.True(LongPath.FileExists(Path.Combine(session, "2024-06-01.cosessiondb")));
        Assert.True(LongPath.DirectoryExists(Path.Combine(session, "Output")));
        Assert.True(LongPath.DirectoryExists(Path.Combine(session, "Trash")));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// A session inside another is its own: its sidecars are offered once, under it, and whether it
    /// is open is asked of its own file. Walking into it from the outer session would offer the same
    /// folder twice, and ask the outer session's file about it.
    /// </summary>
    [Fact]
    public async Task ASessionInsideAnotherIsItsOwn()
    {
        var outer = Session("Outer");
        var inner = Session(Path.Combine("Outer", "Output", "Inner"));
        List(Path.Combine(outer, "Outer.cosessiondb"), Path.Combine(inner, "Inner.cosessiondb"));

        var liveTrees = new FakeLiveTreeInspector().WithHeldFile(Path.Combine(inner, "Inner.cosessiondb"), "CaptureOne");
        var plan = await CreateProvider(liveTrees).PlanAsync();

        Assert.Equal(
            [
                CacheOf(Path.Combine(outer, "Capture", "CaptureOne")),
                CacheOf(Path.Combine(outer, "Selects", "CaptureOne")),
            ],
            plan.Steps.OfType<DeleteStep>().Select(s => s.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(CacheOf(Path.Combine(inner, "Capture", "CaptureOne")), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>§5.3 for a session: one whose file Capture One holds open keeps every cache in it.</summary>
    [Fact]
    public async Task ASessionCaptureOneHasOpenIsLeftAlone()
    {
        var session = Session("Studio");
        List(Path.Combine(session, "Studio.cosessiondb"));

        var liveTrees = new FakeLiveTreeInspector().WithHeldFile(Path.Combine(session, "Studio.cosessiondb"), "CaptureOne");
        var plan = await CreateProvider(liveTrees).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(CacheOf(Path.Combine(session, "Capture", "CaptureOne")), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A session whose only folder of images is a link is not "Already clear": nothing behind the link
    /// was examined.
    /// </summary>
    [Fact]
    public async Task ASessionWhoseImagesAreBehindALinkIsNotClear()
    {
        var session = Path.Combine(ShootDrive, "Linked");
        WriteFile(Path.Combine(session, "Linked.cosessiondb"));
        var elsewhere = Path.Combine(_temp.Path, "elsewhere", "Capture");
        WriteFile(Path.Combine(elsewhere, "CaptureOne", "Settings166", "IMG_0001.CR3.cos"));
        Populate(Path.Combine(elsewhere, "CaptureOne", "Cache", "Proxies"));
        SymbolicLink.ToDirectory(Path.Combine(session, "Capture"), elsewhere);
        List(Path.Combine(session, "Linked.cosessiondb"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(session, "Capture"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.2: a folder called <c>CaptureOne</c> is anybody's. Without one of Capture One's settings
    /// folders beside its <c>Cache</c>, it is not recognised.
    /// </summary>
    [Fact]
    public async Task ACaptureOneFolderWithNoSettingsIsNotOffered()
    {
        var session = Path.Combine(ShootDrive, "Plain");
        WriteFile(Path.Combine(session, "Plain.cosessiondb"));
        var cache = Populate(Path.Combine(session, "Exports", "CaptureOne", "Cache"));
        Directory.CreateDirectory(Path.Combine(session, "Exports", "CaptureOne", "SettingsBackup"));
        List(Path.Combine(session, "Plain.cosessiondb"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.True(LongPath.DirectoryExists(cache));
    }

    /// <summary>
    /// A session file left in the profile, or in an application-data folder, would make everything
    /// there a session to search, Capture One's own styles and presets included.
    /// </summary>
    [Theory]
    [InlineData("profile")]
    [InlineData("local")]
    [InlineData("roaming")]
    [InlineData("inside-local")]
    [InlineData("inside-roaming")]
    public async Task ASessionFolderHoldingTheProfileOrItsDataIsNotSearched(string where)
    {
        var folder = where switch
        {
            "profile" => _environment.UserProfile,
            "local" => _environment.LocalAppData,
            "inside-local" => Path.Combine(_environment.LocalAppData, "CaptureOne", "Styles"),
            "inside-roaming" => Path.Combine(_environment.RoamingAppData, "Capture One", "Presets"),
            _ => _environment.RoamingAppData,
        };

        WriteFile(Path.Combine(folder, "Stray.cosessiondb"));
        var sidecar = Path.Combine(folder, "Pictures", "CaptureOne");
        Directory.CreateDirectory(Path.Combine(sidecar, "Settings166"));
        Populate(Path.Combine(sidecar, "Cache"));
        List(Path.Combine(folder, "Stray.cosessiondb"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.2: a catalog database outside a <c>.cocatalog</c> package does not make the folder around it
    /// a catalog, so the <c>Cache</c> beside it is somebody else's.
    /// </summary>
    [Fact]
    public async Task ALooseCatalogDatabaseDoesNotMakeItsFolderACatalog()
    {
        var folder = Path.Combine(ShootDrive, "Documents");
        WriteFile(Path.Combine(folder, "Copy.cocatalogdb"));
        var cache = Populate(CacheOf(folder));
        List(Path.Combine(folder, "Copy.cocatalogdb"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
        Assert.True(LongPath.DirectoryExists(cache));
    }

    /// <summary>
    /// A folder called <c>Cache</c> in a session that is not inside a <c>CaptureOne</c> folder is the
    /// user's own, whatever its name says.
    /// </summary>
    [Fact]
    public async Task ACacheFolderOutsideASidecarIsNotOffered()
    {
        var session = Session("Studio");
        var own = Populate(Path.Combine(session, "Output", "Cache"));
        List(Path.Combine(session, "Studio.cosessiondb"));

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(plan.Steps.OfType<DeleteStep>(), s => s.Path.Equals(own, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, plan.Steps.Count);
    }

    /// <summary>A session whose file is gone from its folder is not recognised, and nothing in it is offered.</summary>
    [Fact]
    public async Task AListedSessionWithNoSessionFileIsLeftAlone()
    {
        var session = Session("Gone");
        File.Delete(Path.Combine(session, "Gone.cosessiondb"));
        List(Path.Combine(session, "Gone.cosessiondb"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(session, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A <c>Cache</c> that is a link points at a tree nobody classified.</summary>
    [Fact]
    public async Task ACatalogCacheThatIsALinkIsDeclined()
    {
        var catalog = Path.Combine(ShootDrive, "Linked.cocatalog");
        WriteFile(Path.Combine(catalog, "Linked.cocatalogdb"));
        var elsewhere = Populate(Path.Combine(_temp.Path, "elsewhere"));
        SymbolicLink.ToDirectory(CacheOf(catalog), elsewhere);
        List(catalog);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(CacheOf(catalog), StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(elsewhere, "data.bin")));
    }

    /// <summary>
    /// §5.3: a catalog whose database Capture One holds open is being used, so its cache is held
    /// back, protected and named. Another catalog in the same list is still offered.
    /// </summary>
    [Fact]
    public async Task ACatalogCaptureOneHasOpenIsLeftAlone()
    {
        var open = Catalog("Open");
        var closed = Catalog("Closed");
        List(open, closed);

        var liveTrees = new FakeLiveTreeInspector().WithHeldFile(Path.Combine(open, "Open.cocatalogdb"), "CaptureOne");
        var plan = await CreateProvider(liveTrees).PlanAsync();

        Assert.Equal([CacheOf(closed)], plan.Steps.OfType<DeleteStep>().Select(s => s.Path));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(CacheOf(open), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("Open.cocatalog", StringComparison.Ordinal));
    }

    /// <summary>The check is asked again at the clean, so a catalog opened after the preview keeps its cache.</summary>
    [Fact]
    public async Task ACatalogOpenedAfterThePreviewKeepsItsCache()
    {
        var catalog = Catalog("Later");
        List(catalog);

        var liveTrees = new FakeLiveTreeInspector();
        var provider = CreateProvider(liveTrees);
        var plan = await provider.PlanAsync();

        Assert.Single(plan.Steps);

        liveTrees.WithHeldFile(Path.Combine(catalog, "Later.cocatalogdb"), "CaptureOne");
        await provider.ExecuteAsync(plan);

        Assert.True(LongPath.DirectoryExists(CacheOf(catalog)));
    }

    /// <summary>The catalog's <c>writelock</c> is asked about too, as Capture One's own mark that it is open.</summary>
    [Fact]
    public async Task AHeldWriteLockKeepsTheCatalogsCache()
    {
        var catalog = Catalog("Locked");
        WriteFile(Path.Combine(catalog, "writelock"));
        List(catalog);

        var liveTrees = new FakeLiveTreeInspector().WithHeldFile(Path.Combine(catalog, "writelock"), "CaptureOne");
        var plan = await CreateProvider(liveTrees).PlanAsync();

        Assert.Empty(plan.Steps);
    }

    /// <summary>
    /// A catalog Capture One lists only on a drive that is not connected was never looked in, so the
    /// row is not "Already clear".
    /// </summary>
    [Fact]
    public async Task OnlyADisconnectedCatalogIsNotClear()
    {
        var missing = Path.Combine(_temp.Path, "unplugged", "Away.cocatalog");
        List(missing);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(missing, StringComparison.Ordinal));
    }

    /// <summary>
    /// A catalog on a drive that is not connected is the offline case the tier exists for. Nothing
    /// there can be examined, and the plan says so rather than reporting it clear.
    /// </summary>
    [Fact]
    public async Task ACatalogOnADisconnectedDriveIsNamed()
    {
        var present = Catalog("Here");
        var missing = Path.Combine(_temp.Path, "unplugged", "Away.cocatalog");
        List(present, missing);

        var plan = await CreateProvider().PlanAsync();

        Assert.Single(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains(missing, StringComparison.Ordinal));
    }

    /// <summary>Settings Deguffer could not read may list any catalog, so the plan cannot claim to be complete.</summary>
    [Fact]
    public async Task UnreadableSettingsAreAWarningAndNotAClearDisk()
    {
        var folder = Path.Combine(_environment.LocalAppData, "Capture_One", "CaptureOne.exe_StrongName_abc", "16.4.0.0");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, CaptureOneDocuments.SettingsFileName), "<configuration><broken>");

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>
    /// Settings that cannot be listed at all are not "Capture One was never used": the row is present
    /// and says what it could not read.
    /// </summary>
    [Fact]
    public async Task AnUnlistableSettingsFolderIsPresentAndAWarning()
    {
        var company = Path.Combine(_environment.LocalAppData, "Capture_One");
        Directory.CreateDirectory(Path.Combine(company, "CaptureOne.exe_StrongName_abc"));

        using var denied = new DeniedDirectory(company);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(company, StringComparison.Ordinal));
    }

    /// <summary>
    /// The shell makes one column per facet label, so a plan holding both a catalog and a session
    /// must give every item the same label.
    /// </summary>
    [Fact]
    public async Task CatalogsAndSessionsShareOneColumn()
    {
        var catalog = Catalog("Weddings");
        var session = Session("Studio");
        List(catalog, Path.Combine(session, "Studio.cosessiondb"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(3, plan.Steps.Count);
        Assert.Single(plan.Steps.OfType<DeleteStep>().SelectMany(s => s.Facets).Select(f => f.Label).Distinct());
    }

    /// <summary>An empty cache is no step: there is nothing to reclaim.</summary>
    [Fact]
    public async Task AnEmptyCacheIsNoStep()
    {
        var catalog = Path.Combine(ShootDrive, "Fresh.cocatalog");
        WriteFile(Path.Combine(catalog, "Fresh.cocatalogdb"));
        Directory.CreateDirectory(Path.Combine(catalog, "Cache", "Thumbnails"));
        List(catalog);

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
    }

    /// <summary>§5.3's warning names Capture One where it is running and something is offered.</summary>
    [Fact]
    public async Task ARunningCaptureOneIsNamed()
    {
        List(Catalog("Weddings"));

        var plan = await CreateProvider(inspector: new FakeProcessInspector("CaptureOne")).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains("CaptureOne", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7.1: Explore reads the same rule. A catalog and a sidecar each recognise their <c>Cache</c>
    /// and nothing else, so the photographs and the edits beside it are refused there too.
    /// </summary>
    [Fact]
    public async Task ToolRootsRecogniseOnlyTheCache()
    {
        var catalog = Catalog("Weddings");
        var session = Session("Studio");
        List(catalog, Path.Combine(session, "Studio.cosessiondb"));

        var roots = await CreateProvider().DiscoverToolRootsAsync();

        var catalogRoot = Assert.Single(roots, r => r.Path.Equals(catalog, StringComparison.OrdinalIgnoreCase));
        Assert.True(catalogRoot.Recognises("Cache"));
        Assert.False(catalogRoot.Recognises("Originals"));
        Assert.False(catalogRoot.Recognises("Adjustments"));

        var sidecar = Assert.Single(roots, r => r.Path.Equals(Path.Combine(session, "Capture", "CaptureOne"), StringComparison.OrdinalIgnoreCase));
        Assert.True(sidecar.Recognises("Cache"));
        Assert.False(sidecar.Recognises("Settings166"));

        Assert.DoesNotContain(roots, r => r.Path.Equals(session, StringComparison.OrdinalIgnoreCase));
    }
}
