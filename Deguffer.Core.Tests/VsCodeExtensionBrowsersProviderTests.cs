using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The browser an editor extension downloads sits four folders deep in the editor's <c>User</c>
/// folder, beside the extension's conversation history and checkpoints. The rules worth proving are
/// that only the named marker finds a cache, that only Puppeteer's own builds inside it are offered,
/// and §5.6's, that every sibling at every level from the extension's folder down survives.
/// </summary>
public sealed class VsCodeExtensionBrowsersProviderTests : IDisposable
{
    private const string Cline = "saoudrizwan.claude-dev";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public VsCodeExtensionBrowsersProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private VsCodeExtensionBrowsersProvider CreateProvider(ILiveTreeInspector? liveTrees = null) =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive);

    /// <summary>An editor's user-data folder holding both of the markers its discovery requires.</summary>
    private string CreateEditor(string name = "Code")
    {
        var path = Path.Combine(_environment.RoamingAppData, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Local State"), "{}");
        CreateFile(Path.Combine(path, "User", "globalStorage", "state.vscdb"));
        return path;
    }

    private static string GlobalStorage(string editor) => Path.Combine(editor, "User", "globalStorage");

    /// <summary>The extension's folder, holding what Cline keeps beside its browser.</summary>
    private static string CreateExtension(string editor, string extension = Cline)
    {
        var folder = Path.Combine(GlobalStorage(editor), extension);
        CreateFile(Path.Combine(folder, "tasks", "1720000000000", "api_conversation_history.json"));
        CreateFile(Path.Combine(folder, "checkpoints", "1234", ".git", "HEAD"));
        CreateFile(Path.Combine(folder, "settings", "cline_mcp_settings.json"));
        return folder;
    }

    /// <summary>The cache root inside an extension's folder.</summary>
    private static string CacheRoot(string extensionFolder) =>
        Path.Combine(extensionFolder, "puppeteer", ".chromium-browser-snapshots");

    /// <summary>Create the given folders under the cache root, each holding a payload.</summary>
    private static string CreateBuilds(string extensionFolder, params string[] relatives)
    {
        var root = CacheRoot(extensionFolder);
        Directory.CreateDirectory(root);

        foreach (var relative in relatives)
        {
            CreateFile(Path.Combine(root, relative, "chrome-win", "chrome.exe"), 4096);
        }

        return root;
    }

    private static string CreateFile(string path, int bytes = 64)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task ReportsNotPresentWhenNoExtensionDownloadedABrowser()
    {
        CreateExtension(CreateEditor());

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>The resolver creates the cache folder before its first download, so an empty one is no row.</summary>
    [Fact]
    public async Task AnEmptyCacheIsNotPresent()
    {
        CreateBuilds(CreateExtension(CreateEditor()));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task TargetsTheChromiumBuildClineDownloaded()
    {
        var extension = CreateExtension(CreateEditor());
        var root = CreateBuilds(extension, @"chromium\win64-1108766");

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(root, "chromium", "win64-1108766")], plan.TargetedPaths);
    }

    /// <summary>
    /// Tier 1, unlike Puppeteer's own cache: the resolver downloads a missing build again by itself,
    /// so the next use is a wait and never a failure.
    /// </summary>
    [Fact]
    public void IsTierOneBecauseTheExtensionDownloadsTheBrowserAgainByItself()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableCache, provider.Tier);
        Assert.True(provider.Tier.IsPreSelectedByDefault());
    }

    /// <summary>
    /// The marker is what identifies the cache. The same builds anywhere else in the extension's folder,
    /// or under an extension that put them in a folder of another name, are not found at all.
    /// </summary>
    [Theory]
    [InlineData(@".chromium-browser-snapshots\chromium\win64-1108766")]            // no 'puppeteer' above it
    [InlineData(@"puppeteer\chromium\win64-1108766")]                              // no marker
    [InlineData(@"puppeteer\.local-chromium\chromium\win64-1108766")]              // another name
    [InlineData(@"tasks\puppeteer\.chromium-browser-snapshots\chromium\win64-1108766")] // a level too deep
    public async Task FindsNothingThatIsNotUnderTheMarker(string relative)
    {
        var extension = CreateExtension(CreateEditor());
        CreateFile(Path.Combine(extension, relative, "chrome-win", "chrome.exe"), 4096);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// A folder that is not an editor's is never searched, however exactly the path inside it matches:
    /// the discovery's two markers come first.
    /// </summary>
    [Fact]
    public async Task IgnoresAFolderThatIsNotAnEditors()
    {
        var impostor = Path.Combine(_environment.RoamingAppData, "NotAnEditor");
        CreateBuilds(Path.Combine(impostor, "User", "globalStorage", Cline), @"chromium\win64-1108766");

        Assert.False(await CreateProvider().IsPresentAsync());
        Assert.Empty((await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// §5.2's dangerous direction inside the cache: a folder that is not a build of its browser, or a
    /// browser folder Puppeteer does not name, is left alone, named, and asserted to survive.
    /// </summary>
    [Theory]
    [InlineData(@"chromium\win64-127.0.6533.88", @"chromium\win64-127.0.6533.88")]   // a Chrome version under Chromium
    [InlineData(@"chromium\notes", @"chromium\notes")]                               // something a person made
    [InlineData("win64-1108766", "win64-1108766")]                                   // a build with no browser folder above it
    [InlineData(@"webkit\win64-1108766", "webkit")]                                  // not a browser Puppeteer downloads
    public async Task LeavesWhatItDoesNotRecogniseInTheCacheAloneAndSaysSo(string relative, string declinedRelative)
    {
        var root = CreateBuilds(CreateExtension(CreateEditor()), relative);
        var declined = Path.Combine(root, declinedRelative);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains($"Leaving '{Path.GetFileName(declined)}' alone", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == declined && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>
    /// §5.6 at every level. The extension's folder, <c>puppeteer</c> and the cache root survive, and so
    /// does everything beside each of them: the conversation history and the checkpoints are the
    /// reason this row may never be one level too wide.
    /// </summary>
    [Fact]
    public async Task AssertsEverySiblingFromTheExtensionsFolderDownSurvives()
    {
        var editor = CreateEditor();
        var extension = CreateExtension(editor);
        var root = CreateBuilds(extension, @"chromium\win64-1108766");
        var puppeteer = Path.GetDirectoryName(root)!;
        var besidePuppeteer = Path.Combine(puppeteer, "notes");
        Directory.CreateDirectory(besidePuppeteer);
        var metadata = CreateFile(Path.Combine(root, "chromium", ".metadata"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Path.Combine(root, "chromium", "win64-1108766")], plan.TargetedPaths);

        foreach (var survivor in new[]
                 {
                     extension,
                     Path.Combine(extension, "tasks"),
                     Path.Combine(extension, "checkpoints"),
                     Path.Combine(extension, "settings"),
                     puppeteer,
                     besidePuppeteer,
                     root,
                     Path.Combine(root, "chromium"),
                     metadata,
                 })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path == survivor && p.PresenceBefore is PathPresence.Present);
        }

        Assert.DoesNotContain(plan.TargetedPaths, p => !p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every editor's extensions are searched, and each build carries its editor and extension in its
    /// identity, so a kept one in one editor does not keep the same build in another.
    /// </summary>
    [Fact]
    public async Task FindsTheCacheInEveryEditorAndKeepsEachBuildApart()
    {
        var code = CreateBuilds(CreateExtension(CreateEditor("Code")), @"chromium\win64-1108766");
        var cursor = CreateBuilds(CreateExtension(CreateEditor("Cursor"), "rooveterinaryinc.roo-cline"), @"chromium\win64-1108766");

        var steps = (await CreateProvider().PlanAsync()).Steps
            .OfType<DeleteStep>()
            .ToDictionary(step => step.Path, StringComparer.OrdinalIgnoreCase);

        var inCode = steps[Path.Combine(code, "chromium", "win64-1108766")];
        Assert.Equal($"Code/{Cline}/chromium/win64-1108766", inCode.Identity?.Key);
        Assert.Equal($"{Cline} in Code", inCode.Group);
        Assert.Equal([new ItemFacet("Version", "1108766"), new ItemFacet("Platform", "win64")], inCode.Facets);

        var inCursor = steps[Path.Combine(cursor, "chromium", "win64-1108766")];
        Assert.Equal("Cursor/rooveterinaryinc.roo-cline/chromium/win64-1108766", inCursor.Identity?.Key);
    }

    /// <summary>
    /// The marker path is built from constants, so a link at any segment of it would put the removal on
    /// the far side while every survivor resolved through the same link. It is named and not followed.
    /// </summary>
    [Theory]
    [InlineData("puppeteer")]
    [InlineData(@"puppeteer\.chromium-browser-snapshots")]
    public async Task DeclinesALinkOnTheWayToTheCache(string relative)
    {
        var extension = CreateExtension(CreateEditor());

        // Laid out so the far side holds a build whichever segment the link replaces.
        var far = Path.Combine(_temp.Path, "far");
        CreateFile(Path.Combine(far, ".chromium-browser-snapshots", "chromium", "win64-1108766", "chrome.exe"), 4096);
        CreateFile(Path.Combine(far, "chromium", "win64-1108766", "chrome.exe"), 4096);

        var link = Path.Combine(extension, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        SymbolicLink.ToDirectory(link, far);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == link);
    }

    /// <summary>
    /// A browser the extension launched runs from inside its build. That build is held back and named,
    /// and the build beside it carries the question to ask again at the clean.
    /// </summary>
    [Fact]
    public async Task HoldsBackABuildARunningBrowserIsUsing()
    {
        var root = CreateBuilds(CreateExtension(CreateEditor()), @"chromium\win64-1108766", @"chromium\win64-1300313");
        var running = Path.Combine(root, "chromium", "win64-1300313");
        var idle = Path.Combine(root, "chromium", "win64-1108766");

        var liveTrees = FakeLiveTreeInspector.NothingLive
            .WithProgram("chrome", executable: Path.Combine(running, "chrome-win", "chrome.exe"));

        var plan = await CreateProvider(liveTrees).PlanAsync();

        Assert.Equal([idle], plan.TargetedPaths);
        Assert.NotNull(plan.Steps.OfType<DeleteStep>().Single().UseCheck);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == running);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("the chromium build 'win64-1300313'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A global storage folder that will not be listed is said so, and the row is present, because a
    /// row that never appears is the one state nothing downstream can correct.
    /// </summary>
    [Fact]
    public async Task AGlobalStorageFolderThatWillNotBeListedIsSaidSo()
    {
        var editor = CreateEditor();
        CreateBuilds(CreateExtension(editor), @"chromium\win64-1108766");
        var storage = GlobalStorage(editor);

        using var denied = new DeniedDirectory(storage);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(storage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryBuildThePlanCleans()
    {
        CreateBuilds(CreateExtension(CreateEditor()), @"chromium\win64-1108766");
        CreateBuilds(CreateExtension(CreateEditor("Cursor")), @"chromium\win64-1300313");

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>
    /// §7.1 reads the same rule from outside. With the editor's own declaration beside this one, a build
    /// may go from Explore and nothing above it or beside it may.
    /// </summary>
    [Theory]
    [InlineData(@"puppeteer\.chromium-browser-snapshots\chromium\win64-1108766", true)]
    [InlineData(@"puppeteer\.chromium-browser-snapshots\chromium\win64-1108766\chrome-win", true)]
    [InlineData(@"puppeteer\.chromium-browser-snapshots\chromium\notes", false)]
    [InlineData(@"puppeteer\.chromium-browser-snapshots\chromium", false)]
    [InlineData(@"puppeteer\.chromium-browser-snapshots", false)]
    [InlineData("puppeteer", false)]
    [InlineData("tasks", false)]
    [InlineData("checkpoints", false)]
    [InlineData("", false)]
    public void ExploreMayRemoveOnlyTheBuilds(string relative, bool allowed)
    {
        var extension = CreateExtension(CreateEditor());
        CreateBuilds(extension, @"chromium\win64-1108766", @"chromium\notes");

        var roots = new VsCodeCacheProvider(_environment).ToolRoots.Concat(CreateProvider().ToolRoots).ToList();
        var policy = new Deguffer.Core.Exploring.Acting.ExploreActionPolicy([], roots, new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? extension : Path.Combine(extension, relative)).IsAllowed);
    }
}
