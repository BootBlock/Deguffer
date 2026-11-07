using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// CapCut's caches. Its <c>Cache</c> folder mixes what the editor makes again with material drafts
/// use, so what has to be shown is that only the recognised children go, that the drafts, the settings,
/// the builds and every other child of <c>Cache</c> survive, and that nothing goes while the editor runs.
/// </summary>
public sealed class CapCutCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public CapCutCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string CapCut => Path.Combine(_environment.LocalAppData, "CapCut");

    private string Cache => Path.Combine(CapCut, "User Data", "Cache");

    private string Thumbnails => Path.Combine(Cache, "frameThumbnail");

    private string Subtitles => Path.Combine(Cache, "recognize");

    private string Effects => Path.Combine(Cache, "effect");

    private string Imported => Path.Combine(Cache, "importcache3");

    private string Drafts => Path.Combine(CapCut, "User Data", "Projects", "com.lveditor.draft");

    private string Config => Path.Combine(CapCut, "User Data", "Config");

    private CapCutCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    private static string Populate(string folder, string file, int bytes = 4096)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, file), new byte[bytes]);
        return folder;
    }

    /// <summary>The tree of an installation that has edited one draft with an effect and subtitles.</summary>
    private void CreateLayout()
    {
        Populate(Thumbnails, "a1b2c3.jpg", 8192);
        Populate(Subtitles, "clip-0001.wav", 16384);
        Populate(Path.Combine(Effects, "7012345678901234567"), "a3f9.bundle");
        Populate(Imported, "IMG_0001.png");
        Populate(Path.Combine(Drafts, "0401"), "draft_content.json");
        Populate(Config, "globalSetting");
        Populate(Path.Combine(CapCut, "Apps", "3.8.0.1431"), "CapCut.exe");
    }

    [Fact]
    public async Task ReportsNotPresentWhereCapCutHasNeverRun()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// The researched Tier 1 row: everything it removes is a cache, and still nothing is ticked for the
    /// user, because nobody here has seen an installation.
    /// </summary>
    [Fact]
    public async Task PlansOnlyTheRecognisedChildrenAndDoesNotPreSelectThem()
    {
        CreateLayout();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { Thumbnails, Subtitles }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal(8192 + 16384, plan.EstimatedBytes);
        Assert.Equal(ProviderBasis.Researched, provider.Basis);
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
    }

    /// <summary>§5.2 and §5.6: drafts, settings, builds, draft material and an unknown child all survive, asserted by name.</summary>
    [Fact]
    public async Task EverythingButTheRecognisedChildrenSurvivesARunAndIsAsserted()
    {
        CreateLayout();
        var unknown = Populate(Path.Combine(Cache, "SomethingNew"), "state.bin");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in new[] { CapCut, Effects, Imported, Path.Combine(CapCut, "User Data", "Projects"), Config, Path.Combine(CapCut, "Apps") })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Thumbnails));
        Assert.False(Directory.Exists(Subtitles));
        Assert.All(
            [Cache, Effects, Imported, Drafts, Config, unknown, Path.Combine(CapCut, "Apps", "3.8.0.1431")],
            path => Assert.True(Directory.Exists(path), $"{path} went with CapCut's cache."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheDraftsVanished()
    {
        CreateLayout();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(Path.Combine(CapCut, "User Data", "Projects"), recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
    }

    /// <summary>The same tree under Jianying Pro's own folder is reached the same way.</summary>
    [Fact]
    public async Task ReachesJianyingProsCacheToo()
    {
        var jianying = Path.Combine(_environment.LocalAppData, "JianyingPro", "User Data", "Cache");
        Populate(Path.Combine(jianying, "audioWave"), "wave.bin");
        var effect = Populate(Path.Combine(jianying, "effect"), "a3f9.bundle");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Path.Combine(jianying, "audioWave")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(effect, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task ALinkedCacheChildIsNamedRatherThanFollowed(DirectoryLinkKind kind)
    {
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"), "irreplaceable.bin");
        Directory.CreateDirectory(Cache);
        DirectoryLink.Create(kind, Thumbnails, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(outside, "irreplaceable.bin")), "a linked cache folder was deleted through.");
    }

    [Fact]
    public async Task NothingIsOfferedWhileTheEditorRuns()
    {
        CreateLayout();

        var provider = CreateProvider(new FakeProcessInspector("CapCut"));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("CapCut is running", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Thumbnails, StringComparison.OrdinalIgnoreCase));

        var discovered = await provider.DiscoverToolRootsAsync();
        Assert.False(new ExploreActionPolicy([], [.. provider.ToolRoots, .. discovered], new FakeVolumeInventory()).MayRemove(Thumbnails).IsAllowed);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(Thumbnails));
    }

    [Fact]
    public async Task AnEditorStartedAfterThePreviewHoldsTheCacheBack()
    {
        CreateLayout();

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.TargetedPaths);

        inspector.WithRunning("JianyingPro").PassTheReadingLimit();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(Thumbnails), "a cache folder was removed under an editor started after the preview");
        Assert.True(Directory.Exists(Subtitles), "a cache folder was removed under an editor started after the preview");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Theory]
    [InlineData(@"User Data\Cache\frameThumbnail", true)]
    [InlineData(@"User Data\Cache\recognize", true)]
    [InlineData(@"User Data\Cache\effect", false)]
    [InlineData(@"User Data\Cache\importcache3", false)]
    [InlineData(@"User Data\Cache\prerender", false)]
    [InlineData(@"User Data\Cache\SomethingNew", false)]
    [InlineData(@"User Data\Cache", false)]
    [InlineData(@"User Data\Projects", false)]
    [InlineData(@"User Data\Config", false)]
    [InlineData("User Data", false)]
    [InlineData("", false)]
    public void ExploreReadsCapCutsFolderFromTheDeclaration(string relative, bool allowed)
    {
        CreateLayout();
        foreach (var name in new[] { "prerender", "SomethingNew" })
        {
            Directory.CreateDirectory(Path.Combine(Cache, name));
        }

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(relative.Length == 0 ? CapCut : Path.Combine(CapCut, relative)).IsAllowed);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        CreateLayout();

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }
}
