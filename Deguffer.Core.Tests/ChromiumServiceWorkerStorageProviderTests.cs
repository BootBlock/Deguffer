using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The offline storage a site's service worker keeps, in the same Chromium folders as the engine's
/// caches. What is shared with <see cref="ChromiumCacheProvider"/> — identification, the credential
/// files, links, the in-use veto — is proved there and in the host and WebView2 tests. These prove
/// what is this row's own: its one name, its tier, and what it leaves beside that name.
/// </summary>
public sealed class ChromiumServiceWorkerStorageProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ChromiumServiceWorkerStorageProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private ChromiumServiceWorkerStorageProvider CreateProvider(ChromiumUserDataDiscovery? discovery = null) =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
            liveTrees: FakeLiveTreeInspector.NothingLive, discovery: discovery);

    private ChromiumCacheProvider CreateCacheProvider(ChromiumUserDataDiscovery? discovery = null) =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
            liveTrees: FakeLiveTreeInspector.NothingLive, discovery: discovery);

    /// <summary>A folder holding Chromium's <c>Local State</c> marker, which identifies it.</summary>
    private string CreateApplication(string name)
    {
        var path = Path.Combine(_environment.LocalAppData, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"<REDACTED>\"}}");
        return path;
    }

    /// <summary>Create a directory holding one file, so it measures as non-empty.</summary>
    private static string CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[4096]);
        return path;
    }

    /// <summary>
    /// §3's Tier 2, which is the reason this row exists apart from the engine's caches: a web
    /// application that worked offline stops doing so until it is next online, so the row is offered
    /// and never pre-selected. A Tier 1 answer here pre-selects that loss for everyone who presses
    /// Clean.
    /// </summary>
    [Fact]
    public async Task PlansEachProfilesOfflineStorageAtTier2AndIsNeverPreSelected()
    {
        var app = CreateApplication("Browserish");
        var shared = CreateDirectory(Path.Combine(app, "Service Worker", "CacheStorage"));
        var work = CreateDirectory(Path.Combine(app, "Default", "Service Worker", "CacheStorage"));
        var personal = CreateDirectory(Path.Combine(app, "Profile 1", "Service Worker", "CacheStorage"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { shared, work, personal }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);

        var finding = new Finding(provider, IsPresent: true, plan);

        Assert.True(finding.HasSomethingToRemove);
        Assert.False(finding.IsPreSelectedByDefault);
    }

    /// <summary>
    /// The engine's caches are the other row's subject, and a folder holding only those is no source
    /// for this one. Reporting it present would offer a row the plan then has nothing to say about.
    /// </summary>
    [Fact]
    public async Task AFolderHoldingOnlyTheEnginesCachesIsNotPresence()
    {
        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "GPUCache"));
        CreateDirectory(Path.Combine(app, "Cache", "Cache_Data"));
        CreateDirectory(Path.Combine(app, "Service Worker", "ScriptCache"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// Any directory may be called <c>Service Worker</c>, so a folder that has not been identified as
    /// Chromium's is never looked inside, whatever it holds.
    /// </summary>
    [Fact]
    public async Task AStorageNameAloneIsNotLicenceToLookInsideAFolder()
    {
        var impostor = Path.Combine(_environment.LocalAppData, "SomeOtherApplication");
        var storage = CreateDirectory(Path.Combine(impostor, "Service Worker", "CacheStorage"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).TargetedPaths);
        Assert.True(Directory.Exists(storage));
    }

    /// <summary>
    /// §5.2 and §5.6 together. The service worker's registration and its script are what refill the
    /// storage the next time its site is opened, and an invented name stands for whatever a future
    /// Chromium version writes beside them, and the site data and credentials belong to no row. Every
    /// one of them is asserted to survive, not merely left out of the plan.
    ///
    /// <para>The engine's caches are the other row's to take, so this row neither asserts them nor
    /// counts them as left alone, and still leaves them standing when it is cleaned on its own.</para>
    /// </summary>
    [Fact]
    public async Task EverythingBesideTheOfflineStorageIsAssertedToSurviveIt()
    {
        var app = CreateApplication("Browserish");
        var profile = Path.Combine(app, "Default");
        var storage = CreateDirectory(Path.Combine(profile, "Service Worker", "CacheStorage"));

        string[] directories =
        [
            CreateDirectory(Path.Combine(profile, "Service Worker", "Database")),
            CreateDirectory(Path.Combine(profile, "Service Worker", "ScriptCache")),
            CreateDirectory(Path.Combine(profile, "Service Worker", "Something_New")),
            CreateDirectory(Path.Combine(profile, "IndexedDB")),
            CreateDirectory(Path.Combine(profile, "Local Storage")),
            Path.Combine(profile, "Service Worker"),
            profile,
            app,
        ];

        var cookies = Path.Combine(profile, "Cookies");
        var logins = Path.Combine(profile, "Login Data");
        File.WriteAllText(cookies, "<REDACTED>");
        File.WriteAllText(logins, "<REDACTED>");
        string[] files = [cookies, logins, Path.Combine(app, "Local State")];

        var engineCache = CreateDirectory(Path.Combine(profile, "GPUCache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([storage], plan.TargetedPaths);
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(engineCache, StringComparison.OrdinalIgnoreCase));

        foreach (var path in directories.Concat(files))
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(storage));
        Assert.All(directories, path => Assert.True(Directory.Exists(path), $"{path} was removed."));
        Assert.All(files, path => Assert.True(File.Exists(path), $"{path} was removed."));
        Assert.True(Directory.Exists(engineCache), "the other row's cache was removed by this one.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// <c>Service Worker</c> is reached by name rather than by an enumeration that filters links, so
    /// without the check a junctioned one puts the deletion of <c>CacheStorage</c> wherever the link
    /// points, while every survivor named inside the profile resolves through it and passes.
    /// </summary>
    [Fact]
    public async Task AJunctionedServiceWorkerDirectoryIsNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "CacheStorage"));

        var app = CreateApplication("Chatter");
        SymbolicLink.ToDirectory(Path.Combine(app, "Service Worker"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("Service Worker", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "planning looked through a junctioned 'Service Worker' and deleted the far side.");
    }

    /// <summary>
    /// The directory that is kept is the reason for the sentence: a user who sees
    /// <c>Service Worker</c> still standing after a clean cannot otherwise tell that what was inside
    /// it went, or that the service workers themselves were kept.
    /// </summary>
    [Fact]
    public async Task ThePlanSaysTheServiceWorkersStayRegistered()
    {
        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "Service Worker", "CacheStorage"));
        CreateDirectory(Path.Combine(app, "Service Worker", "Database"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("'Chatter'", StringComparison.Ordinal) &&
            n.Message.Contains("stay registered", StringComparison.Ordinal));
    }

    /// <summary>
    /// The two rows share one folder and are cleaned in one run, each asserting that what it spared
    /// survived. Each spares what the other removes, so a run that read the other row's removal as
    /// this row's over-reach would fail every clean that ticked both.
    /// </summary>
    [Fact]
    public async Task BothRowsCleanedTogetherEachPassTheirOwnVerification()
    {
        var app = CreateApplication("Browserish");
        var gpuCache = CreateDirectory(Path.Combine(app, "Default", "GPUCache"));
        var storage = CreateDirectory(Path.Combine(app, "Default", "Service Worker", "CacheStorage"));
        var database = CreateDirectory(Path.Combine(app, "Default", "Service Worker", "Database"));

        var discovery = new ChromiumUserDataDiscovery(_environment);
        var planner = new CleanupPlanner([CreateCacheProvider(discovery), CreateProvider(discovery)]);
        var findings = await planner.PlanAllAsync();

        var results = await planner.ExecuteAsync(
            findings,
            [.. findings.Select(f => new Confirmation(f.Provider.Id))]);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Verification!.Passed, r.Verification.Summary));
        Assert.False(Directory.Exists(gpuCache));
        Assert.False(Directory.Exists(storage));
        Assert.True(Directory.Exists(database));
    }

    /// <summary>
    /// The two rows share one walk of the application-data roots, memoised for a planning pass. An
    /// application that appears between passes must still be seen once the pass starts again, or a
    /// browser installed while Deguffer was open stays invisible to both rows until it restarts.
    /// </summary>
    [Fact]
    public async Task ASharedDiscoveryStillSeesAnApplicationAddedBetweenPasses()
    {
        var discovery = new ChromiumUserDataDiscovery(_environment);
        var provider = CreateProvider(discovery);

        Assert.False(await provider.IsPresentAsync());

        var storage = CreateDirectory(Path.Combine(CreateApplication("Browserish"), "Service Worker", "CacheStorage"));

        provider.InvalidateCaches();

        Assert.True(await provider.IsPresentAsync());
        Assert.Equal([storage], (await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// A plan carries this row's tier, not the child's, so a name added to the table at Tier 1 would
    /// be planned and confirmed at Tier 2, and one added at Tier 3 would lose its warning.
    /// </summary>
    [Fact]
    public void EveryDeclaredChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();

        foreach (var level in ChromiumServiceWorkerStorageProvider.Levels)
        {
            foreach (var name in level.Children.DisposableNames)
            {
                Assert.Equal(provider.Tier, level.Children.Classify(name).Tier);
            }
        }
    }

    /// <summary>
    /// One name. <c>ScriptCache</c> and <c>Database</c> sit beside it and are what make the storage
    /// come back, so a second name appearing here would be a change of what this row is.
    /// </summary>
    [Fact]
    public void TheTableDeclaresOnlyTheOfflineStorage()
    {
        Assert.Equal(
            ["CacheStorage"],
            ChromiumServiceWorkerStorageProvider.Levels.SelectMany(l => l.Children.DisposableNames));
    }

    /// <summary>
    /// Each row leaves out of its survivors what a sibling's table offers, and it learns the
    /// siblings from one list. A row built on the same class and missing from that list would have
    /// every removal it makes reported by the others as a §5.6 failure whenever both are ticked.
    /// </summary>
    [Fact]
    public void EveryChromiumRowIsInTheFamilyItsSiblingsConsult()
    {
        var rows = typeof(ChromiumUserDataProvider).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(ChromiumUserDataProvider)))
            .Select(t => (IReadOnlyList<CacheLevel>)t.GetField("Levels")!.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(rows);
        Assert.Equal(rows.Count, ChromiumUserDataProvider.Family.Count);
        Assert.All(rows, levels => Assert.Contains(ChromiumUserDataProvider.Family, f => ReferenceEquals(f, levels)));
    }

    /// <summary>
    /// The two rows divide one folder between them, so no name may be offered by both: a directory
    /// in both tables would be pre-selected under the Tier 1 row whatever this row's tier says.
    /// </summary>
    [Fact]
    public void NoNameIsOfferedByBothRows()
    {
        var mine = ChromiumServiceWorkerStorageProvider.Levels
            .SelectMany(l => l.Children.DisposableNames.Select(name => Path.Combine(l.ContainerName, name)));
        var theirs = ChromiumCacheProvider.Levels
            .SelectMany(l => l.Children.DisposableNames.Select(name => Path.Combine(l.ContainerName, name)));

        Assert.Empty(mine.Intersect(theirs, StringComparer.OrdinalIgnoreCase));
    }
}
