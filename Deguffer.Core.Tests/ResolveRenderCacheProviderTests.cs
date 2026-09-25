using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// DaVinci Resolve's render cache, found in a <c>CacheClip</c> folder at the root of a drive or in the
/// Videos folder.
///
/// <para>Three things have to hold. Only a project folder holding a render file is ever a target, and
/// <c>OptimizedMedia</c> never is, although it holds the same files. Everything beside the cache
/// survives a run, asserted by name, because beside it sit the user's backups, recordings, stills and
/// proxies. And nothing is offered or removed while Resolve runs.</para>
/// </summary>
public sealed class ResolveRenderCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes = new();

    public ResolveRenderCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        Directory.CreateDirectory(Drive);
        _volumes.With(Drive);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A drive whose root is Resolve's first Media Storage location.</summary>
    private string Drive => Path.Combine(_temp.Path, "media-drive");

    private ResolveRenderCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, volumes: _volumes);

    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4096]);
        return path;
    }

    private static string CacheIn(string folder) => Path.Combine(folder, ResolveCacheLayout.CacheFolderName);

    /// <summary>One project's render cache, nested as Resolve nests it, with a render file deep inside.</summary>
    private static string Project(string cache, string id)
    {
        var project = Path.Combine(cache, id);
        WriteFile(Path.Combine(project, "clip-7", "0001.dvcc"));
        return project;
    }

    [Fact]
    public async Task ReportsNotPresentWhereResolveHasCachedNothing()
    {
        Directory.CreateDirectory(Path.Combine(Drive, "ProjectBackup"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
        Assert.Empty(await provider.DiscoverToolRootsAsync());
    }

    [Fact]
    public void IsTierOneAndItsPartsAreOneDecision()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableCache, provider.Tier);
        Assert.Equal(StepGrain.Parts, provider.Grain);
    }

    /// <summary>
    /// §5.2 and §5.6: each project folder holding render files is a target, and everything else in the
    /// cache folder and beside it survives the run, on the disk as in the report. Optimised media holds
    /// the same <c>.dvcc</c> files, so only its name keeps it out, and that is asserted here.
    /// </summary>
    [Fact]
    public async Task OffersOnlyProjectRenderCachesAndEverythingBesideThemSurvives()
    {
        var cache = CacheIn(Drive);
        var first = Project(cache, "8b1f0c2a4e");
        var second = Project(cache, "c93d7e5f10");
        var optimised = WriteFile(Path.Combine(cache, "OptimizedMedia", "a1b2c3", "0001.dvcc"));
        var proxy = WriteFile(Path.Combine(cache, "ProxyMedia", "A001_C002.mov"));
        var stranger = WriteFile(Path.Combine(cache, "Notes", "readme.txt"));
        var index = WriteFile(Path.Combine(cache, "index.db"));

        var backup = WriteFile(Path.Combine(Drive, "ProjectBackup", "Feature.drp"));
        var recording = WriteFile(Path.Combine(Drive, "Capture", "Voiceover.wav"));
        var live = WriteFile(Path.Combine(Drive, "Resolve Live", "Feature", "snapshot.dpx"));
        var stills = WriteFile(Path.Combine(Drive, ".gallery", "Still_1.1.1.dpx"));
        var proxies = WriteFile(Path.Combine(Drive, "ProxyMedia", "A001_C002.mov"));
        var footage = WriteFile(Path.Combine(Drive, "Footage", "A001_C002.braw"));

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { first, second }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var survivor in new[]
        {
            cache,
            Path.Combine(cache, "OptimizedMedia"),
            Path.Combine(cache, "ProxyMedia"),
            Path.GetDirectoryName(stranger)!,
            index,
            Path.Combine(Drive, "ProjectBackup"),
            Path.Combine(Drive, "Capture"),
            Path.Combine(Drive, "Resolve Live"),
            Path.Combine(Drive, ".gallery"),
            Path.Combine(Drive, "ProxyMedia"),
        })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase));
        }

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);

        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));

        foreach (var kept in new[] { optimised, proxy, stranger, index, backup, recording, live, stills, proxies, footage })
        {
            Assert.True(File.Exists(kept), $"'{kept}' did not survive the run");
        }

        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// §5.2's unrecognised case: a folder in <c>CacheClip</c> with no render file in it is not a
    /// project's render cache, whatever it holds. A cache folder holding nothing else offers nothing,
    /// and says so rather than reading as a machine without Resolve.
    /// </summary>
    [Fact]
    public async Task AFolderWithNoRenderFileIsNotRenderCache()
    {
        var cache = CacheIn(Drive);
        var unknown = WriteFile(Path.Combine(cache, "Exports", "Feature.mov"));

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("holds any render cache", StringComparison.Ordinal));
        Assert.False(Assert.Single(await provider.DiscoverToolRootsAsync()).Recognises("Exports"));
        Assert.True(File.Exists(unknown));
    }

    /// <summary>
    /// §7.1: Explore may take from <c>CacheClip</c> only what the plan recognised, so optimised media
    /// and anything unrecognised in it are refused there too.
    /// </summary>
    [Fact]
    public async Task ExploreIsToldOnlyWhatThePlanRecognises()
    {
        var cache = CacheIn(Drive);
        Project(cache, "8b1f0c2a4e");
        WriteFile(Path.Combine(cache, "OptimizedMedia", "a1b2c3", "0001.dvcc"));
        WriteFile(Path.Combine(cache, "Notes", "readme.txt"));

        var root = Assert.Single(await CreateProvider().DiscoverToolRootsAsync());

        Assert.Equal(cache, root.Path, ignoreCase: true);
        Assert.True(root.Recognises("8b1f0c2a4e"));
        Assert.False(root.Recognises("OptimizedMedia"));
        Assert.False(root.Recognises("Notes"));
    }

    /// <summary>With no Media Storage location set, one report finds the cache in the Videos folder.</summary>
    [Fact]
    public async Task FindsTheCacheInTheVideosFolder()
    {
        var project = Project(CacheIn(_environment.Videos!), "8b1f0c2a4e");

        Assert.Equal([project], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    [Fact]
    public async Task LooksNowhereWhenWindowsWillNotSayWhereTheVideosFolderIs()
    {
        Project(CacheIn(_environment.Videos!), "8b1f0c2a4e");
        _environment.WithNoVideos();

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// A share, a drive with nothing in it and a drive whose content is stored elsewhere are not this
    /// computer's disk, so a cache folder on any of them is not looked for.
    /// </summary>
    [Fact]
    public async Task LooksOnlyOnLocalDrivesThatHoldTheirOwnContent()
    {
        var share = Path.Combine(_temp.Path, "share");
        var empty = Path.Combine(_temp.Path, "card-reader");
        var cloud = Path.Combine(_temp.Path, "cloud");

        foreach (var root in new[] { share, empty, cloud })
        {
            Project(CacheIn(root), "8b1f0c2a4e");
        }

        _volumes
            .With(share, DriveType.Network)
            .With(empty, DriveType.Removable, isReady: false)
            .With(cloud, features: VolumeFeatures.RemoteStorage);

        var removable = Path.Combine(_temp.Path, "usb-ssd");
        var project = Project(CacheIn(removable), "8b1f0c2a4e");
        _volumes.With(removable, DriveType.Removable);

        Assert.Equal([project], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// §5.3: Resolve writes the cache while it runs, so nothing is offered, the cache folder is named,
    /// the row does not read as clear, and Explore refuses everything in it.
    /// </summary>
    [Fact]
    public async Task NothingIsOfferedWhileResolveRuns()
    {
        var cache = CacheIn(Drive);
        var project = Project(cache, "8b1f0c2a4e");

        var provider = CreateProvider(new FakeProcessInspector("Resolve"));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(cache, StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase));
        Assert.False(Assert.Single(await provider.DiscoverToolRootsAsync()).Recognises("8b1f0c2a4e"));
        Assert.True(Directory.Exists(project));
    }

    /// <summary>The clean asks again, so Resolve opened while the preview was on screen holds the cache back.</summary>
    [Fact]
    public async Task ResolveStartedAfterThePreviewHoldsTheCacheBack()
    {
        var project = Project(CacheIn(Drive), "8b1f0c2a4e");

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.Equal([project], plan.TargetedPaths);

        inspector.WithRunning("Resolve");
        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(project), "a render cache was removed under a Resolve started after the preview");
        Assert.Equal("Nothing was removed: Resolve is running now.", Assert.Single(result.Steps).Message);
    }

    /// <summary>A cache folder that is a link is declined and named, and nothing it points at is touched.</summary>
    [Fact]
    public async Task ACacheFolderThatIsALinkIsLeftAlone()
    {
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        var project = Project(elsewhere, "8b1f0c2a4e");
        SymbolicLink.ToDirectory(CacheIn(Drive), elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));
        Assert.True(Directory.Exists(project));
    }

    /// <summary>A project folder that is a link is declined, while its real neighbours are offered.</summary>
    [Fact]
    public async Task AProjectFolderThatIsALinkIsLeftAlone()
    {
        var cache = CacheIn(Drive);
        var real = Project(cache, "8b1f0c2a4e");
        var elsewhere = Project(Path.Combine(_temp.Path, "elsewhere"), "c93d7e5f10");
        var link = Path.Combine(cache, "c93d7e5f10");
        SymbolicLink.ToDirectory(link, elsewhere);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([real], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(elsewhere, "clip-7", "0001.dvcc")));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>A cache folder Windows will not list is a warning, never a machine with nothing cached.</summary>
    [Fact]
    public async Task ACacheFolderThatWillNotBeListedIsReportedAsUnread()
    {
        var cache = CacheIn(Drive);
        Project(cache, "8b1f0c2a4e");

        using var denied = new DeniedDirectory(cache);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(cache, StringComparison.Ordinal));
    }

    /// <summary>A rescan looks again, so a cache written since the last one is found.</summary>
    [Fact]
    public async Task ARescanFindsACacheWrittenSinceTheLastOne()
    {
        var provider = CreateProvider();

        Assert.True((await provider.PlanAsync()).IsEmpty);

        var project = Project(CacheIn(Drive), "8b1f0c2a4e");
        provider.InvalidateCaches();

        Assert.Equal([project], (await provider.PlanAsync()).TargetedPaths);
    }
}
