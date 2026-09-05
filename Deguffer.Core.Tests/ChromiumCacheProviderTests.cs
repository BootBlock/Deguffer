using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// A Chromium user-data folder is the most dangerous neighbourhood any provider works in: the six
/// disposable directories sit among sign-in tokens, saved passwords, drafts and offline data, in
/// the same folder and in the same naming style. So these are mostly negative tests. The positive
/// ones only establish that the six are reached at all.
/// </summary>
public sealed class ChromiumCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ChromiumCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private ChromiumCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>
    /// A folder holding Chromium's <c>Local State</c> marker, which is what identifies it as a
    /// user-data folder at all.
    /// </summary>
    private string CreateApplication(string name, string? root = null)
    {
        var path = Path.Combine(root ?? _environment.RoamingAppData, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"<REDACTED>\"}}");
        return path;
    }

    /// <summary>
    /// A cache level is reached by name, and a full path resolves through a directory the account
    /// may not list — listing and traversing are separate rights. So <c>Cache</c> can hold the web
    /// cache, answer the presence probe with it, refuse the listing that would classify
    /// <c>Cache_Data</c>, and leave the provider announcing that no application on this machine
    /// keeps a Chromium cache at all — one pass, two contradictory statements.
    /// </summary>
    [Fact]
    public async Task ACacheLevelThatWillNotBeListedIsSaidSoRatherThanReportedAsNoCacheAtAll()
    {
        var application = CreateApplication("Chatter");
        var container = Path.Combine(application, "Cache");
        CreateDirectory(Path.Combine(container, "Cache_Data"));

        using var denied = new DeniedDirectory(container);

        var provider = CreateProvider();

        // The premise: the by-name probe still reaches the web cache through the refused container.
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(container));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("No application on this machine keeps a Chromium cache"));
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// The refusal one level up, at an application-data root, which is the case this provider gets
    /// wrong in a way no other provider can.
    ///
    /// <para>Every other provider decides presence by probing a path it already knows the name of,
    /// and a full path resolves through a directory the account may not list. This one decides by
    /// <em>enumerating</em> both application-data roots, so a refusal there answers "no source at
    /// all" — and the planner never calls <see cref="ChromiumCacheProvider.PlanAsync"/> for a
    /// provider that reports itself absent. The row then reads "Not installed", which is a
    /// stronger untruth than the "Already clear" the rest of this work exists to stop.</para>
    /// </summary>
    [Fact]
    public async Task AnApplicationDataRootThatWillNotBeListedIsSaidSoRatherThanReportedAsAbsent()
    {
        using var denied = new DeniedDirectory(_environment.RoamingAppData);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(
            plan.Notes,
            n => n.Message.Contains(_environment.RoamingAppData, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("No application on this machine keeps a Chromium cache"));
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// The same refusal while the <em>other</em> root still turns up an application. Reading the
    /// refused roots only where nothing was found dropped the fact in exactly the case where a plan
    /// gets rendered and read, so the sentence has to survive a successful pass too.
    /// </summary>
    [Fact]
    public async Task ARefusedRootIsStillReportedWhenTheOtherRootFindsAnApplication()
    {
        var application = CreateApplication("Chatter", _environment.LocalAppData);
        CreateDirectory(Path.Combine(application, "GPUCache"));

        using var denied = new DeniedDirectory(_environment.RoamingAppData);

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(
            plan.Notes,
            n => n.Severity == PlanNoteSeverity.Warning
                && n.Message.Contains(_environment.RoamingAppData, StringComparison.Ordinal));

        // The application the readable root did turn up is still planned normally.
        Assert.Contains(Path.Combine(application, "GPUCache"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Create a directory holding one file, so it measures as non-empty.</summary>
    private static string CreateDirectory(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task ReportsNotPresentWhenNoApplicationKeepsAChromiumCache()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// The dangerous direction named in <c>docs/todo/unreached-locations.md</c> §4: treating a
    /// coincidental cache name as licence. Any directory anywhere may be called <c>GPUCache</c>, so
    /// a folder that has not been identified as Chromium's is never looked inside.
    /// </summary>
    [Fact]
    public async Task ACacheNameAloneIsNotLicenceToLookInsideAFolder()
    {
        var impostor = Path.Combine(_environment.RoamingAppData, "SomeOtherApplication");
        var cache = CreateDirectory(Path.Combine(impostor, "GPUCache"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.Applications());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(cache));
    }

    /// <summary>
    /// The Unreal lesson from §8 of the same document: a folder existing is not evidence that the
    /// cache inside it does. An application that embeds Chromium but has not run yet keeps a
    /// user-data folder with nothing disposable in it.
    /// </summary>
    [Fact]
    public async Task AUserDataFolderWithNoCacheInItIsNotPresence()
    {
        var app = CreateApplication("Notes");
        CreateDirectory(Path.Combine(app, "Local Storage"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansAllSixCacheNamesIncludingTheTwoThatAreGrandchildren()
    {
        var app = CreateApplication("Chatter");

        var codeCache = CreateDirectory(Path.Combine(app, "Code Cache"));
        var gpuCache = CreateDirectory(Path.Combine(app, "GPUCache"));
        var graphite = CreateDirectory(Path.Combine(app, "DawnGraphiteCache"));
        var webGpu = CreateDirectory(Path.Combine(app, "DawnWebGPUCache"));
        var httpCache = CreateDirectory(Path.Combine(app, "Cache", "Cache_Data"));
        var cacheStorage = CreateDirectory(Path.Combine(app, "Service Worker", "CacheStorage"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { cacheStorage, codeCache, graphite, webGpu, gpuCache, httpCache }
                .Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
    }

    /// <summary>
    /// Both application-data roots are scanned. An application that keeps its user data under
    /// <c>%LOCALAPPDATA%</c> rather than <c>%APPDATA%</c> is the same shape and the same decision.
    /// </summary>
    [Fact]
    public async Task ScansBothApplicationDataRoots()
    {
        var roaming = CreateDirectory(
            Path.Combine(CreateApplication("Roamer", _environment.RoamingAppData), "GPUCache"));
        var local = CreateDirectory(
            Path.Combine(CreateApplication("Localiser", _environment.LocalAppData), "GPUCache"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            new[] { local, roaming }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.2. The user-data folder is a tool root in every sense that matters, and so are the two
    /// containers this provider descends into: <c>Service Worker</c> holds registrations and scripts
    /// beside the cached responses, and <c>Cache</c> is spared for the reason any unclassified
    /// parent is — the rule takes the child it recognises, never the directory holding it.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheUserDataFolderOrTheContainersItDescendsInto()
    {
        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "Cache", "Cache_Data"));
        CreateDirectory(Path.Combine(app, "Service Worker", "CacheStorage"));

        var plan = await CreateProvider().PlanAsync();

        foreach (var root in new[] { app, Path.Combine(app, "Cache"), Path.Combine(app, "Service Worker") })
        {
            Assert.DoesNotContain(root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(plan.TargetedPaths, path =>
                Assert.False(IsAtOrUnder(root, path), $"{path} would have taken {root} with it."));

            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(root, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    /// <summary>
    /// §5.2's dangerous direction, and the whole reason this provider is an exact allow-list. Every
    /// one of these sits in the same folder in the same naming style as the six, two of them with
    /// the word "Cache" in the name, and every one of them is user data or live state.
    /// </summary>
    [Theory]
    [InlineData("Local Storage")]
    [InlineData("Session Storage")]
    [InlineData("IndexedDB")]
    [InlineData("Extensions")]
    [InlineData("Sync Data")]
    [InlineData("SuperCache")]
    public async Task AnUnrecognisedSiblingIsTier4AndIsAssertedToSurvive(string name)
    {
        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "GPUCache"));
        var sibling = CreateDirectory(Path.Combine(app, name));

        var profileLevel = ChromiumCacheProvider.Levels.Single(l => l.ContainerName.Length == 0);
        Assert.Equal(SafetyTier.DoNotTouch, profileLevel.Children.Classify(name).Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        // Not merely absent from the plan — asserted to survive (§5.6).
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(sibling), $"{name} was removed alongside the caches.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The same rule one level down, inside the two directories this provider deliberately reaches
    /// into. <c>ScriptCache</c> and <c>Database</c> are the real neighbours of <c>CacheStorage</c>,
    /// observed in a live <c>Service Worker</c> directory. <c>Cache</c> was observed holding nothing
    /// but <c>Cache_Data</c>, so the third subject is an invented name: what has to hold is that
    /// anything appearing there in a future Chromium version is spared, and there is no way to test
    /// that against a name that exists today.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedChildInsideAContainerIsTier4AndSurvives()
    {
        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "Cache", "Cache_Data"));
        CreateDirectory(Path.Combine(app, "Service Worker", "CacheStorage"));

        var unexpected = CreateDirectory(Path.Combine(app, "Cache", "Something_New"));
        var scripts = CreateDirectory(Path.Combine(app, "Service Worker", "ScriptCache"));
        var database = CreateDirectory(Path.Combine(app, "Service Worker", "Database"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var spared in new[] { unexpected, scripts, database })
        {
            Assert.DoesNotContain(spared, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(spared, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(new[] { unexpected, scripts, database }, path => Assert.True(Directory.Exists(path)));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The <c>accounts</c> lesson from the shader caches, in a folder with a great deal more to
    /// lose. Child classification enumerates directories, so a file beside the caches is never seen
    /// and never asserted unless the provider names it — and these three files are the sign-in
    /// state, the saved passwords and the key that decrypts both.
    /// </summary>
    [Fact]
    public async Task TheCredentialFilesAreAssertedToSurviveEvenThoughTheyAreNeverClassified()
    {
        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "GPUCache"));

        var cookies = Path.Combine(app, "Cookies");
        var logins = Path.Combine(app, "Login Data");
        var cards = Path.Combine(app, "Web Data");
        var localState = Path.Combine(app, "Local State");
        File.WriteAllText(cookies, "<REDACTED>");
        File.WriteAllText(logins, "<REDACTED>");
        File.WriteAllText(cards, "<REDACTED>");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var file in new[] { cookies, logins, cards, localState })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(file, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(new[] { cookies, logins, cards, localState }, file => Assert.True(File.Exists(file)));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Chromium moved <c>Cookies</c> under <c>Network</c>, so both paths are named. The one that is
    /// not there records itself as nothing to preserve rather than as a survival that never
    /// happened.
    /// </summary>
    [Fact]
    public async Task TheModernCookiesLocationIsCoveredAsWellAsTheOldOne()
    {
        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "GPUCache"));

        Directory.CreateDirectory(Path.Combine(app, "Network"));
        var cookies = Path.Combine(app, "Network", "Cookies");
        File.WriteAllText(cookies, "<REDACTED>");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(cookies, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(app, "Cookies"), StringComparison.OrdinalIgnoreCase) && !p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(cookies));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7's per-step selection is what gives per-profile control, so each profile's caches are
    /// their own steps. A user who keeps a work profile signed in and a personal one dormant can
    /// clear one and leave the other.
    /// </summary>
    [Fact]
    public async Task EachProfileGetsItsOwnStepsSoOneCanBeKept()
    {
        var app = CreateApplication("Browserish");
        var shared = CreateDirectory(Path.Combine(app, "GPUCache"));
        var work = CreateDirectory(Path.Combine(app, "Default", "Code Cache"));
        var personal = CreateDirectory(Path.Combine(app, "Profile 1", "Code Cache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { shared, work, personal }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        var narrowed = plan.NarrowedTo(
            [.. plan.Steps.Where(s => s is DeleteDirectoryStep d && d.Path.Equals(work, StringComparison.OrdinalIgnoreCase))]);

        var result = await provider.ExecuteAsync(narrowed);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(work));
        Assert.True(Directory.Exists(personal), "the profile the user kept was cleared as well.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A profile directory is a known word and a number, on Playwright's pattern. Anything else is
    /// never looked inside — which matters because a folder the user made themselves is exactly
    /// where a hand-taken backup of a profile would sit.
    /// </summary>
    [Fact]
    public async Task ADirectoryThatOnlyLooksLikeAProfileIsNeverLookedInside()
    {
        var app = CreateApplication("Browserish");
        CreateDirectory(Path.Combine(app, "GPUCache"));
        var backup = CreateDirectory(Path.Combine(app, "Profile backup", "Code Cache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(backup, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        // The folder is a spared sibling of the profiles that are entered, so §5.6 asserts it
        // rather than merely leaving it out.
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(app, "Profile backup"), StringComparison.OrdinalIgnoreCase) &&
            p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(backup), "a hand-made profile copy was cleared.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7 scopes the age column to per-workspace and per-project data. Each of these is one whole
    /// cache for one profile, so a timestamp on it would be a number with nothing to mean — and six
    /// different dates for one application would invite the user to read a difference between them.
    /// </summary>
    [Fact]
    public async Task NoStepCarriesAnAgeBecauseTheseAreWholeCaches()
    {
        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "GPUCache"));
        CreateDirectory(Path.Combine(app, "Cache", "Cache_Data"));

        var plan = await CreateProvider().PlanAsync();

        Assert.NotEmpty(plan.Steps);
        Assert.All(plan.Steps, step => Assert.Null(step.LastWritten));
    }

    /// <summary>
    /// A junctioned container is the worst case this provider has. <c>Cache</c> is reached by name
    /// rather than by an enumeration that filters links, so without the check the deletion of
    /// <c>Cache_Data</c> lands wherever the link points, while every §5.6 survivor named inside the
    /// profile resolves through it and passes.
    /// </summary>
    [Fact]
    public async Task AJunctionedContainerIsNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "Cache_Data"));

        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "GPUCache"));
        Directory.CreateSymbolicLink(Path.Combine(app, "Cache"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(
            Path.Combine(outside, "Cache_Data"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.Combine(app, "Cache", "Cache_Data"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("Cache", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "planning looked through a junctioned container and deleted the far side.");
    }

    /// <summary>
    /// A junctioned container is met twice — once as a link child of the profile, once as a level
    /// whose own directory turns out to be one — and both times it is the same path. Saying it
    /// twice is a plan that reads as though two things were skipped.
    /// </summary>
    [Fact]
    public async Task AJunctionedContainerIsReportedOnceRatherThanOncePerLevel()
    {
        var outside = CreateDirectory(Path.Combine(_temp.Path, "elsewhere"));

        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "GPUCache"));
        Directory.CreateSymbolicLink(Path.Combine(app, "Cache"), outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Single(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));
    }

    /// <summary>
    /// The discovery walk is the only thing standing between a junctioned data folder and a
    /// deletion on the far side of it, and it was the only link case with no test. A profile
    /// reached through such a folder is a real directory, so every later reparse check answers
    /// false and every §5.6 survivor resolves through the link and passes.
    /// </summary>
    [Fact]
    public async Task AJunctionedApplicationDataFolderIsNeverIdentified()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "Default", "Code Cache"));
        File.WriteAllText(Path.Combine(outside, "Local State"), "{}");

        Directory.CreateSymbolicLink(Path.Combine(_environment.RoamingAppData, "Chatter"), outside);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.Applications());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "discovery looked through a junctioned data folder and deleted the far side.");
    }

    /// <summary>
    /// The same rule one level down. A profile is reached by enumerating the data folder, so the
    /// link filtering there is what keeps a redirected profile out of the plan.
    ///
    /// Unlike its sibling above, this one has two independent guards: removing the enumeration's
    /// link filtering alone leaves <see cref="CacheLevelWalk"/>'s reparse check to decline the profile,
    /// so a mutation of either guard on its own keeps this test green. It fails when both go. That
    /// is defence in depth rather than a weak assertion, and it is written down because a mutation
    /// pass on one guard would otherwise read as a test that proves nothing.
    /// </summary>
    [Fact]
    public async Task AJunctionedProfileIsNeverEntered()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "Code Cache"));

        var app = CreateApplication("Browserish");
        CreateDirectory(Path.Combine(app, "GPUCache"));
        Directory.CreateSymbolicLink(Path.Combine(app, "Default"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(
            Path.Combine(app, "Default", "Code Cache"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "a junctioned profile was entered and the far side deleted.");
    }

    /// <summary>
    /// Two of the six sit inside a directory that is kept, so a user who sees that directory still
    /// standing has no way to tell the cache inside it went. The sentence is said only when it
    /// happened, so a plan that emptied no container must not carry it.
    /// </summary>
    [Fact]
    public async Task TheContainerSentenceAppearsOnlyWhenACacheInsideOneWasRemoved()
    {
        var flat = CreateApplication("Flat");
        CreateDirectory(Path.Combine(flat, "GPUCache"));
        CreateDirectory(Path.Combine(flat, "Local Storage"));

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("Service Worker", StringComparison.Ordinal));

        var nested = CreateApplication("Nested");
        CreateDirectory(Path.Combine(nested, "Cache", "Cache_Data"));

        var provider = CreateProvider();
        var second = await provider.PlanAsync();

        Assert.Contains(second.Notes, n =>
            n.Message.Contains("'Nested'", StringComparison.Ordinal) &&
            n.Message.Contains("Service Worker", StringComparison.Ordinal));
    }

    /// <summary>
    /// A junctioned cache is a child the user can see, so a plan that neither offers it nor mentions
    /// it disagrees with the folder. Dropping it silently would also make the empty-plan message
    /// lie, since presence resolves through the link.
    /// </summary>
    [Fact]
    public async Task AJunctionedCacheIsNamedRatherThanDroppedSilently()
    {
        var outside = CreateDirectory(Path.Combine(_temp.Path, "elsewhere"));

        var app = CreateApplication("Chatter");
        Directory.CreateSymbolicLink(Path.Combine(app, "GPUCache"), outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("GPUCache", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        // The same reasoning as the note above, carried to the row: nothing was targeted and this
        // application's only cache was declined, so the shell must not call the row clear.
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(outside, "entry.bin")), "a junctioned cache was deleted through.");
    }

    /// <summary>
    /// The table is designed to grow, and a child declared above Tier 1 would be planned under this
    /// provider's Tier 1 sentence and pre-selected, because a plan carries the provider's tier
    /// rather than the child's. <see cref="DisposableChildSet"/> offers Tier 2 and Tier 3 names, so
    /// nothing else here would catch it.
    /// </summary>
    [Fact]
    public void EveryDeclaredChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();

        foreach (var level in ChromiumCacheProvider.Levels)
        {
            foreach (var name in level.Children.DisposableNames)
            {
                Assert.Equal(provider.Tier, level.Children.Classify(name).Tier);
            }
        }
    }

    /// <summary>
    /// The six names, and only the six. A seventh appearing in the table without the reasoning that
    /// belongs to it is exactly how a signature stops being a signature.
    /// </summary>
    [Fact]
    public void TheTableDeclaresTheSixChromiumCacheNamesAndNoOthers()
    {
        Assert.Equal(
            ["CacheStorage", "Cache_Data", "Code Cache", "DawnGraphiteCache", "DawnWebGPUCache", "GPUCache"],
            ChromiumCacheProvider.Levels
                .SelectMany(l => l.Children.DisposableNames)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExecutingRemovesTheCachesAndLeavesTheFolderStanding()
    {
        var app = CreateApplication("Chatter");
        var gpuCache = CreateDirectory(Path.Combine(app, "GPUCache"));
        var httpCache = CreateDirectory(Path.Combine(app, "Cache", "Cache_Data"));
        var localStorage = CreateDirectory(Path.Combine(app, "Local Storage"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(gpuCache));
        Assert.False(Directory.Exists(httpCache));

        Assert.True(Directory.Exists(app));
        Assert.True(Directory.Exists(Path.Combine(app, "Cache")));
        Assert.True(Directory.Exists(localStorage));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheUserDataFolderVanished()
    {
        var app = CreateApplication("Chatter");
        CreateDirectory(Path.Combine(app, "GPUCache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch.
        Directory.Delete(app, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(app, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §6.3. A Chromium HTTP cache is a wide store of small entry files under a folder that is
    /// already several segments deep, so measurement and deletion both have to survive content past
    /// MAX_PATH. A smoke test, and knowingly so: .NET prefixes long paths itself before calling
    /// Win32, so what actually proves Core applies the prefix is
    /// <c>DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm</c>. This one
    /// earns its place as a crash guard over a deep tree.
    /// </summary>
    [Fact]
    public async Task MeasuresAndRemovesContentPastMaxPath()
    {
        var app = CreateApplication("Chatter");
        var cache = Path.Combine(app, "Cache", "Cache_Data");

        var deep = cache;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('e', 40));
        }

        var entry = Path.Combine(deep, "entry.bin");
        Assert.True(entry.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(entry), new byte[4096]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes > 0, "A Chromium cache past MAX_PATH was measured as empty.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.FileExists(entry), "An entry past MAX_PATH survived the removal.");
        Assert.False(Directory.Exists(cache));

        // §6.3's failure mode is a silent *partial* deletion, which is exactly the case where the
        // §5.6 negative earns its place: a truncated path can take the wrong tree with it.
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
