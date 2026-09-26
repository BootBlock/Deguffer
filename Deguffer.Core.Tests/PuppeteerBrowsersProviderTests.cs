using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Puppeteer is Playwright's path-based Tier 2 case one level deeper. The rules worth proving are
/// §5.2's at both levels, that a build is recognised only by its own browser's rule and everything
/// else keeps Tier 4, and §5.6's, that the metadata Puppeteer resolves a launch through and the
/// shared <c>.cache</c> folder around it survive.
/// </summary>
public sealed class PuppeteerBrowsersProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public PuppeteerBrowsersProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private PuppeteerBrowsersProvider CreateProvider(ILiveTreeInspector? liveTrees = null) =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive);

    private string DefaultRoot => Path.Combine(_environment.UserProfile, ".cache", "puppeteer");

    /// <summary>Create the given folders under the default root, each holding a payload.</summary>
    private string CreateRoot(params string[] relatives)
    {
        var root = DefaultRoot;
        Directory.CreateDirectory(root);

        foreach (var relative in relatives)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "payload.bin"), new byte[4096]);
        }

        return root;
    }

    [Fact]
    public async Task ReportsNotPresentWhenPuppeteerNeverDownloadedABrowser()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>One build of every browser Puppeteer downloads, in the shape each is published in.</summary>
    [Theory]
    [InlineData(@"chrome\win64-127.0.6533.88")]
    [InlineData(@"chrome\win64-1108766")]                     // Puppeteer 19 and 20 kept Chromium here
    [InlineData(@"chrome-headless-shell\win64-127.0.6533.88")]
    [InlineData(@"chromedriver\win32-127.0.6533.88")]
    [InlineData(@"chromium\win64-1108766")]
    [InlineData(@"firefox\win64-stable_129.0")]
    [InlineData(@"firefox\win64-stable_129.0.2")]
    [InlineData(@"firefox\win64-beta_130.0b5")]
    [InlineData(@"firefox\win64-devedition_130.0b5")]
    [InlineData(@"firefox\win64-esr_128.2.0esr")]
    [InlineData(@"firefox\win64-nightly_131.0a1")]
    [InlineData(@"firefox\win64-113.0a1")]                    // the first releases' bare Nightly
    [InlineData(@"Chrome\Win64-127.0.6533.88")]               // the same folders to a case-insensitive disk
    public async Task TargetsEachBrowsersOwnBuilds(string relative)
    {
        var root = CreateRoot(relative);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Path.Combine(root, relative)], plan.TargetedPaths);
    }

    /// <summary>
    /// Builds are listed under their browser and told apart by version and platform. The identity is
    /// the browser and the build in Puppeteer's spelling, so a kept build stays kept wherever the
    /// cache is and however the folder's case was written.
    /// </summary>
    [Fact]
    public async Task ListsEachBuildUnderItsBrowserWithItsVersionAndPlatform()
    {
        var root = CreateRoot(@"Chrome\win64-127.0.6533.88", @"firefox\win64-stable_129.0");

        var steps = (await CreateProvider().PlanAsync()).Steps
            .OfType<DeleteStep>()
            .ToDictionary(step => step.Path, StringComparer.OrdinalIgnoreCase);

        var chrome = steps[Path.Combine(root, "Chrome", "win64-127.0.6533.88")];
        Assert.Equal("chrome", chrome.Group);
        Assert.Equal(
            [new ItemFacet("Version", "127.0.6533.88"), new ItemFacet("Platform", "win64")],
            chrome.Facets);
        Assert.Equal("chrome/win64-127.0.6533.88", chrome.Identity?.Key);

        var firefox = steps[Path.Combine(root, "firefox", "win64-stable_129.0")];
        Assert.Equal("firefox", firefox.Group);
        Assert.Equal(
            [new ItemFacet("Version", "stable_129.0"), new ItemFacet("Platform", "win64")],
            firefox.Facets);
    }

    /// <summary>
    /// §5.2's dangerous direction at the inner level: a folder that is not a build of this browser
    /// lands in Tier 4, stays out of the plan, is named, and is asserted to survive.
    /// </summary>
    [Theory]
    [InlineData(@"chrome\win64")]                             // a platform, but no build
    [InlineData(@"chrome\win64-")]                            // build missing after the separator
    [InlineData(@"chrome\win64-127.0.6533")]                  // fewer parts than Chrome for Testing
    [InlineData(@"chrome\win64-127.0.6533.88-backup")]        // something a person made
    [InlineData(@"chrome\windows-127.0.6533.88")]             // not a platform Puppeteer names
    [InlineData(@"chrome\Xwin64-127.0.6533.88")]              // prefixed: must not match unanchored
    [InlineData(@"chrome\win64-stable_129.0")]                // a Firefox build under Chrome
    [InlineData(@"chromedriver\win64-1108766")]               // a Chromium revision where none is written
    [InlineData(@"chromium\win64-127.0.6533.88")]             // a Chrome version under Chromium
    [InlineData(@"firefox\win64-canary_129.0")]               // not a Firefox channel
    [InlineData(@"firefox\win64-stable_")]
    public async Task LeavesUnrecognisedBuildsAloneAndSaysSo(string relative)
    {
        var root = CreateRoot(relative);
        var path = Path.Combine(root, relative);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains($"Leaving '{Path.GetFileName(path)}' alone", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == path && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>
    /// The same at the outer level. A folder beside the browsers is not one Puppeteer made, however
    /// build-like the folders inside it are, and nothing under it is examined.
    /// </summary>
    [Theory]
    [InlineData("webkit")]                                    // Playwright's browser, not Puppeteer's
    [InlineData("chrome-backup")]
    [InlineData("notes")]
    public async Task LeavesUnrecognisedBrowserFoldersAloneAndSaysSo(string folder)
    {
        var root = CreateRoot(Path.Combine(folder, "win64-127.0.6533.88"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains($"Leaving '{folder}' alone", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(root, folder) && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>
    /// §5.6. The root, each browser folder and its <c>.metadata</c> are never taken, and neither is the
    /// shared <c>.cache</c> folder or the models beside Puppeteer's folder in it, which are the
    /// costliest thing this row could reach.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheRootTheMetadataOrTheSharedCacheFolder()
    {
        var root = CreateRoot(@"chrome\win64-127.0.6533.88");
        var browser = Path.Combine(root, "chrome");
        var metadata = Path.Combine(browser, ".metadata");
        File.WriteAllText(metadata, """{"aliases":{"stable":"127.0.6533.88"}}""");

        var shared = Path.GetDirectoryName(root)!;
        var models = Path.Combine(shared, "huggingface", "hub");
        Directory.CreateDirectory(models);
        File.WriteAllBytes(Path.Combine(models, "model.safetensors"), new byte[4096]);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Path.Combine(browser, "win64-127.0.6533.88")], plan.TargetedPaths);

        foreach (var survivor in new[] { root, browser, metadata, shared })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path == survivor && p.PresenceBefore is PathPresence.Present);
        }

        Assert.DoesNotContain(plan.TargetedPaths, p => p.StartsWith(Path.Combine(shared, "huggingface"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsTierTwoSoItIsOfferedButNeverPreSelected()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);
        Assert.True(provider.Tier.IsOfferable());
        Assert.False(provider.Tier.IsPreSelectedByDefault());
    }

    /// <summary>
    /// A relocated cache is found through the variable, and <c>.cache</c> is then not Puppeteer's
    /// business at all, so it is not named as a survivor either.
    /// </summary>
    [Fact]
    public async Task HonoursTheEnvironmentVariableThatRelocatesTheCache()
    {
        var elsewhere = Path.Combine(_temp.Path, "browsers");
        var build = Path.Combine(elsewhere, "chrome", "win64-127.0.6533.88");
        Directory.CreateDirectory(build);
        File.WriteAllBytes(Path.Combine(build, "payload.bin"), new byte[4096]);

        _environment.WithEnvironmentVariable(PuppeteerBrowsersProvider.LocationVariable, elsewhere);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([build], plan.TargetedPaths);
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path == Path.Combine(_environment.UserProfile, ".cache"));
    }

    /// <summary>
    /// Puppeteer resolves a relative value against the directory a script runs in, and Deguffer is
    /// not that process. The default is not a silent substitute: the user pointed Puppeteer away
    /// from it.
    /// </summary>
    [Theory]
    [InlineData("browsers")]
    [InlineData(@".\browsers")]
    [InlineData(@"..\browsers")]
    public async Task RefusesToGuessWhenTheConfiguredLocationIsNotAFullPath(string configured)
    {
        CreateRoot(@"chrome\win64-127.0.6533.88");
        _environment.WithEnvironmentVariable(PuppeteerBrowsersProvider.LocationVariable, configured);

        var provider = CreateProvider();

        Assert.Null(provider.ResolveRoot());
        Assert.Empty(provider.ToolRoots);
        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a full path", StringComparison.Ordinal));
    }

    /// <summary>
    /// The root is reached by name from a variable, and the enumeration below it never classifies the
    /// directory it is handed, so a junctioned root would hand back the far side's folders and pass
    /// every §5.6 assertion through the same link.
    /// </summary>
    [Fact]
    public async Task DeclinesARootThatIsItselfALink()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = Path.Combine(outside, "chrome", "win64-127.0.6533.88");
        Directory.CreateDirectory(stranger);
        File.WriteAllBytes(Path.Combine(stranger, "chrome.exe"), new byte[4096]);

        var linked = Path.Combine(_temp.Path, "linked-browsers");
        SymbolicLink.ToDirectory(linked, outside);
        _environment.WithEnvironmentVariable(PuppeteerBrowsersProvider.LocationVariable, linked);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    /// <summary>
    /// A link at either level is named and never followed. A cache whose only contents are links is
    /// present and measures nothing, and must not be called clear.
    /// </summary>
    [Theory]
    [InlineData("chrome")]
    [InlineData(@"chrome\win64-127.0.6533.88")]
    public async Task ACacheWhoseEveryEntryIsALinkIsNotCalledClear(string relative)
    {
        var root = CreateRoot();

        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "chrome.exe"), new byte[65536]);

        var link = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        SymbolicLink.ToDirectory(link, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == link);
        Assert.True(File.Exists(Path.Combine(outside, "chrome.exe")));
    }

    /// <summary>
    /// A browser folder that will not be listed is said so, rather than left looking clear, and the
    /// browsers beside it are still offered.
    /// </summary>
    [Fact]
    public async Task ABrowserFolderThatWillNotBeListedIsSaidSo()
    {
        var root = CreateRoot(@"chrome\win64-127.0.6533.88", @"firefox\win64-stable_129.0");
        var firefox = Path.Combine(root, "firefox");

        using var denied = new DeniedDirectory(firefox);

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(firefox, StringComparison.Ordinal));
        Assert.Equal([Path.Combine(root, "chrome", "win64-127.0.6533.88")], plan.TargetedPaths);
    }

    /// <summary>
    /// §5.3. A browser a script launched runs from inside its build, and removing the build under it
    /// fails part of the way through on the files it holds, leaving a build Puppeteer can neither
    /// launch nor recognise as missing. It is held back, named, and asserted to survive, and the
    /// build beside it carries the question to ask again at the clean.
    /// </summary>
    [Fact]
    public async Task HoldsBackABuildARunningBrowserIsUsing()
    {
        var root = CreateRoot(@"chrome\win64-127.0.6533.88", @"chrome\win64-126.0.6478.126");
        var running = Path.Combine(root, "chrome", "win64-127.0.6533.88");
        var idle = Path.Combine(root, "chrome", "win64-126.0.6478.126");

        var liveTrees = FakeLiveTreeInspector.NothingLive
            .WithProgram("chrome", executable: Path.Combine(running, "chrome-win64", "chrome.exe"));

        var plan = await CreateProvider(liveTrees).PlanAsync();

        Assert.Equal([idle], plan.TargetedPaths);
        Assert.NotNull(plan.Steps.OfType<DeleteStep>().Single().UseCheck);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == running);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("the chrome build 'win64-127.0.6533.88'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A check that could not run must not look like one that found nothing. The builds are still
    /// offered, as every other row offers on a partial answer, and the plan says so.
    /// </summary>
    [Fact]
    public async Task SaysSoWhenItCannotTellWhetherABuildIsInUse()
    {
        CreateRoot(@"chrome\win64-127.0.6533.88");

        var plan = await CreateProvider(FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Single(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("could not check whether anything is using these", StringComparison.Ordinal));
    }
}
