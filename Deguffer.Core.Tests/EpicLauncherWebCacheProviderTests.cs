using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// A web cache folder is a Chromium profile directory, and the whole safety argument for this
/// provider is that it reaches into one rather than removing it. So these are mostly negative tests:
/// that the folder itself is never a target, that the sign-in cookies and the store's web storage
/// survive, that a name the table does not carry is left alone, and that a link anywhere on the
/// derived path stops the pass rather than redirecting it.
/// </summary>
public sealed class EpicLauncherWebCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public EpicLauncherWebCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private EpicLauncherWebCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string Saved => Path.Combine(_environment.LocalAppData, "EpicGamesLauncher", "Saved");

    /// <summary>Create a directory holding one file, so it measures as non-empty.</summary>
    private static string CreateDirectory(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[bytes]);
        return path;
    }

    /// <summary>
    /// One web cache folder as the launcher writes it: the three caches, the container holding two
    /// of them with its register beside them, and the store's own data.
    /// </summary>
    private WebCacheFixture AddWebCache(string name = "webcache_4430")
    {
        var root = Path.Combine(Saved, name);
        var serviceWorker = Path.Combine(root, "Service Worker");

        var caches = new[]
        {
            CreateDirectory(Path.Combine(root, "Cache")),
            CreateDirectory(Path.Combine(root, "Code Cache")),
            CreateDirectory(Path.Combine(serviceWorker, "CacheStorage")),
            CreateDirectory(Path.Combine(serviceWorker, "ScriptCache")),
        };

        var kept = new[]
        {
            CreateDirectory(Path.Combine(serviceWorker, "Database")),
            CreateDirectory(Path.Combine(root, "Local Storage")),
            CreateDirectory(Path.Combine(root, "Session Storage")),
            CreateDirectory(Path.Combine(root, "IndexedDB")),
        };

        var cookies = Path.Combine(root, "Cookies");
        File.WriteAllText(cookies, "<REDACTED>");

        return new WebCacheFixture(root, serviceWorker, caches, [.. kept, cookies]);
    }

    /// <summary>The settings and saved state that sit in the same directory listing (§5.2).</summary>
    private string[] AddLauncherState()
    {
        string[] names = ["Config", "Data", "Saves", "UserVaultSettings"];

        return [.. names.Select(name => CreateDirectory(Path.Combine(Saved, name)))];
    }

    /// <summary>One web cache folder, split into what may go and what may not.</summary>
    private sealed record WebCacheFixture(
        string Root,
        string ServiceWorker,
        IReadOnlyList<string> Caches,
        IReadOnlyList<string> Kept);

    [Fact]
    public async Task ReportsNotPresentWhenTheLauncherHasNoFolder()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.WebCaches());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// The lesson the Chromium provider records: the folder existing is not evidence that a cache
    /// inside it does. Every machine that has opened the launcher has a <c>Saved</c> folder, so
    /// reading that as a hit would offer a row the plan then has nothing to say about.
    /// </summary>
    [Fact]
    public async Task ReportsNotPresentWhenNothingUnderSavedIsAWebCacheFolder()
    {
        AddLauncherState();
        CreateDirectory(Path.Combine(Saved, "Logs"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.WebCaches());
    }

    /// <summary>
    /// Every folder Epic's own article names, in one pass. The suffix is a launcher build number, so
    /// a machine that has run the launcher for years holds several of these and every one of them
    /// is a cache nobody is going back to.
    /// </summary>
    [Fact]
    public async Task TargetsTheRecognisedCachesInEveryWebCacheFolder()
    {
        var fixtures = new[]
        {
            AddWebCache("webcache"),
            AddWebCache("webcache_4147"),
            AddWebCache("webcache_4430"),
        };

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());
        Assert.Equal(3, provider.WebCaches().Count);

        var plan = await provider.PlanAsync();

        foreach (var cache in fixtures.SelectMany(f => f.Caches))
        {
            Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Equal(12, plan.Steps.Count);
        Assert.True(plan.EstimatedBytes > 0);
    }

    /// <summary>
    /// §5.6, and the reason this provider exists in the shape it does. Epic's own remedy is to
    /// delete the whole folder, which takes the sign-in cookies and the store's web storage with it.
    /// Asserting that the caches went is half a test; this asserts what stayed.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheWebCacheFolderTheSignInCookiesOrTheLauncherState()
    {
        var state = AddLauncherState();
        var fixture = AddWebCache();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        string[] survivors = [Saved, fixture.Root, fixture.ServiceWorker, .. fixture.Kept, .. state];

        foreach (var path in survivors)
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        }

        // Not merely absent from the plan — asserted to survive (§5.6). The container is the one
        // exception: it is spared rather than protected by name, and the assertion below is that
        // it is still standing afterwards.
        foreach (var path in survivors.Where(p => p != fixture.ServiceWorker))
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);

        foreach (var path in survivors)
        {
            Assert.True(
                Directory.Exists(path) || File.Exists(path),
                $"'{path}' was removed alongside the caches.");
        }

        Assert.Equal("<REDACTED>", File.ReadAllText(Path.Combine(fixture.Root, "Cookies")));

        foreach (var cache in fixture.Caches)
        {
            Assert.False(Directory.Exists(cache), $"'{cache}' was not removed.");
        }
    }

    /// <summary>
    /// The credential surface is named in full rather than sampled, on the Chromium provider's
    /// reasoning: a file is never enumerated and so never asserted unless the provider names it, and
    /// anything less makes the §5.6 evidence weaker than the claim it supports. A newer engine build
    /// moves the cookie jar under <c>Network</c>, and the store takes payment, so all four names
    /// belong here.
    /// </summary>
    [Fact]
    public async Task TheWholeCredentialSurfaceIsAssertedToSurvive()
    {
        var fixture = AddWebCache();

        string[] credentials =
        [
            Path.Combine(fixture.Root, "Login Data"),
            Path.Combine(fixture.Root, "Web Data"),
            Path.Combine(fixture.Root, "Network", "Cookies"),
        ];

        foreach (var path in credentials)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "<REDACTED>");
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in credentials)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.All(credentials, path => Assert.Equal("<REDACTED>", File.ReadAllText(path)));
    }

    /// <summary>
    /// A folder that will not be listed is not a folder with nothing in it. Answering "not
    /// installed" here would make a claim about a directory nobody read, and would also leave the
    /// sentence that says so unreachable — the planner never asks an absent provider for a plan.
    /// </summary>
    [Fact]
    public async Task AnUnreadableLauncherFolderIsReportedRatherThanCalledAbsent()
    {
        Directory.CreateDirectory(Saved);

        using var denied = new DeniedDirectory(Saved);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Message.Contains(Saved, StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.2's dangerous direction is an unknown thing treated as safe. A Chromium profile is full of
    /// directories in the same naming style as the caches, so a name the table does not carry has to
    /// land at Tier 4 rather than be guessed at.
    /// </summary>
    [Theory]
    [InlineData("", "Local Storage")]
    [InlineData("", "databases")]
    [InlineData("", "shared_proto_db")]
    [InlineData("", "something-unrecognised")]
    [InlineData("Service Worker", "Database")]
    [InlineData("Service Worker", "something-unrecognised")]
    public async Task AnUnrecognisedChildOfAWebCacheFolderIsNeverTargeted(string container, string name)
    {
        var fixture = AddWebCache();
        var sibling = CreateDirectory(Path.Combine(fixture.Root, container, name));

        Assert.Equal(
            SafetyTier.DoNotTouch,
            EpicLauncherWebCacheProvider.Levels
                .Single(l => l.ContainerName == container)
                .Children.Classify(name)
                .Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(sibling), $"'{name}' was removed alongside the caches.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The pattern is a known word <em>and</em> a number, on Playwright's precedent. A folder that
    /// merely starts with the word is not one of Epic's, and looking inside it would be a guess.
    /// </summary>
    [Theory]
    [InlineData("webcache_backup")]
    [InlineData("webcacheX")]
    [InlineData("webcache_4430_old")]
    [InlineData("mywebcache")]
    public async Task AFolderThatOnlyLooksLikeAWebCacheIsNeverEntered(string name)
    {
        Assert.DoesNotMatch(EpicLauncherSaved.WebCacheDirectory(), name);

        var root = Path.Combine(Saved, name);
        var inside = CreateDirectory(Path.Combine(root, "Cache"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(inside, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The path down to <c>Saved</c> is built from <c>%LOCALAPPDATA%</c> plus two constants rather
    /// than enumerated, so nothing on the way down has been through a filter that separates links
    /// out. A junction at any segment redirects the deletion while every §5.6 survivor named below
    /// resolves through the same link and passes — the vacuous negative.
    /// </summary>
    [Theory]
    [InlineData("EpicGamesLauncher")]
    [InlineData(@"EpicGamesLauncher\Saved")]
    public async Task AJunctionAnywhereOnThePathToTheSavedFolderIsNeverLookedThrough(string relative)
    {
        var outside = _temp.CreateDirectory("elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "webcache_4430", "Cache"));

        var link = Path.Combine(_environment.LocalAppData, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, outside);

        var provider = CreateProvider();

        // Present rather than absent: the row must render the sentence below rather than claim a
        // launcher that is installed is not.
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "planning looked through a link and deleted the far side.");
    }

    /// <summary>
    /// A junctioned web cache folder is a child the user can see, so a plan that neither offers it
    /// nor mentions it disagrees with the folder.
    /// </summary>
    [Fact]
    public async Task AJunctionedWebCacheFolderIsNamedRatherThanDroppedSilently()
    {
        var outside = CreateDirectory(Path.Combine(_temp.Path, "elsewhere", "Cache"));

        Directory.CreateDirectory(Saved);
        Directory.CreateSymbolicLink(
            Path.Combine(Saved, "webcache_4430"), Path.GetDirectoryName(outside)!);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("webcache_4430", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(outside, "entry.bin")),
            "a junctioned web cache folder was deleted through.");
    }

    /// <summary>
    /// The same one level down. <c>Cache</c> arrives from an enumeration that filters links out
    /// today, and that is the point: a safety property riding on a filter nobody named holds only
    /// for as long as every target happens to arrive the same way.
    /// </summary>
    [Fact]
    public async Task AJunctionedCacheInsideAWebCacheFolderIsNamedRatherThanDroppedSilently()
    {
        var fixture = AddWebCache();
        var outside = CreateDirectory(Path.Combine(_temp.Path, "elsewhere"));

        Directory.Delete(Path.Combine(fixture.Root, "Cache"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "Cache"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(
            Path.Combine(fixture.Root, "Cache"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("Cache", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(outside, "entry.bin")),
            "a junctioned cache was deleted through.");
    }

    /// <summary>
    /// The other half of the link rule, and the half a test over a cache child cannot reach. A
    /// <em>level's own directory</em> is reached by name, so <c>DirectoryExists</c> answers through
    /// the junction and the walk would list the far side's ordinary directories — where a recognised
    /// name would be targeted while every §5.6 survivor named for this folder resolved through the
    /// same link and passed.
    /// </summary>
    [Fact]
    public async Task AJunctionedContainerIsNeverListedThrough()
    {
        var fixture = AddWebCache();

        var outside = _temp.CreateDirectory("elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "CacheStorage"));

        Directory.Delete(fixture.ServiceWorker, recursive: true);
        Directory.CreateSymbolicLink(fixture.ServiceWorker, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(bystander, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.Combine(fixture.ServiceWorker, "CacheStorage"),
            plan.TargetedPaths,
            StringComparer.OrdinalIgnoreCase);

        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("Service Worker", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "planning listed through a junctioned container and deleted the far side.");
    }

    /// <summary>
    /// §7.1's second deletion route reads §5.2 out of these declarations rather than restating it,
    /// so a level with no root of its own is a level Explore decides about on its own.
    /// </summary>
    [Fact]
    public void DeclaresARootForTheSavedFolderAndForEveryLevelOfEveryWebCacheFolder()
    {
        var fixture = AddWebCache();

        var roots = CreateProvider().ToolRoots.Select(r => r.Path).ToList();

        Assert.Contains(Saved, roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(fixture.Root, roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(fixture.ServiceWorker, roots, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A web cache folder is not a recognised child of <c>Saved</c>, and that is deliberate: nothing
    /// in Deguffer removes one whole, so Explore must not offer to either.
    /// </summary>
    [Fact]
    public void TheSavedFolderRecognisesNeitherAWebCacheFolderNorTheLauncherState()
    {
        var root = CreateProvider().ToolRoots.Single(r =>
            r.Path.Equals(Saved, StringComparison.OrdinalIgnoreCase));

        Assert.False(root.Recognises("webcache_4430"));
        Assert.False(root.Recognises("Config"));
        Assert.False(root.Recognises("UserVaultSettings"));
    }

    /// <summary>
    /// The row's badge is the provider's tier, so a child offered above it would be cleaned under a
    /// promise §3 does not make for it — and pre-selected, since Tier 1 is.
    /// </summary>
    [Fact]
    public void EveryOfferableChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();

        var offerable =
            from level in EpicLauncherWebCacheProvider.Levels
            from name in level.Children.DisposableNames
            select level.Children.Classify(name);

        Assert.NotEmpty(offerable);
        Assert.All(offerable, child => Assert.Equal(provider.Tier, child.Tier));
    }

    /// <summary>
    /// §6.3. A Chromium disk cache is a wide store of small entry files under a folder already
    /// several segments deep. A smoke test, and knowingly so: .NET prefixes long paths itself before
    /// calling Win32, so what proves Core applies the prefix is
    /// <c>DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm</c>. This one
    /// earns its place as a crash guard over a deep tree.
    /// </summary>
    [Fact]
    public async Task MeasuresAndRemovesContentPastMaxPath()
    {
        var fixture = AddWebCache();
        var cache = Path.Combine(fixture.Root, "Cache");

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

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.FileExists(entry), "An entry past MAX_PATH survived the removal.");
        Assert.False(Directory.Exists(cache));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }
}
