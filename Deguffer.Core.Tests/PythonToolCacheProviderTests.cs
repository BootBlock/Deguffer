using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Python tools' tagged cache folders: pytest, mypy, ruff, tox and nox.
///
/// <para>Every one is recognised by a cache directory tag, and the tag is a convention anybody can
/// follow, so the question each test asks is whether the tag read is the one this tool writes.
/// The fixture writes each file byte for byte as the tool's own source does on Windows.</para>
/// </summary>
public sealed class PythonToolCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;

    public PythonToolCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    public static TheoryData<PythonToolCache> Tools => [.. Enum.GetValues<PythonToolCache>()];

    public static TheoryData<PythonToolCache, SafetyTier> Tiers => new()
    {
        { PythonToolCache.Pytest, SafetyTier.RegenerableCache },
        { PythonToolCache.Mypy, SafetyTier.RegenerableCache },
        { PythonToolCache.Ruff, SafetyTier.RegenerableCache },
        { PythonToolCache.Tox, SafetyTier.RegenerableWithCost },
        { PythonToolCache.Nox, SafetyTier.RegenerableWithCost },
    };

    [Theory]
    [MemberData(nameof(Tiers))]
    public async Task PlansTheFolderAsTheToolWritesItAtItsTier(PythonToolCache tool, SafetyTier tier)
    {
        var cache = PythonToolCacheFixture.Create(Path.Combine(ApproveRoot(), "project"), tool);

        var plan = await ProviderFor(tool).PlanAsync();

        Assert.Equal([cache], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Equal(tier, plan.Tier);
    }

    /// <summary>
    /// Either line ending is the tool's: each of these tools writes one or the other depending on how
    /// it opens the file, and the same tool on another platform writes the other. ruff's tag is the
    /// bare header with no line ending at all, so for ruff only its <c>.gitignore</c> changes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public async Task AcceptsTheTagWithTheOtherLineEnding(PythonToolCache tool)
    {
        var tag = PythonToolCacheFixture.Tag(tool);
        var swapped = tag.Contains("\r\n", StringComparison.Ordinal) ? tag.Replace("\r\n", "\n", StringComparison.Ordinal) : tag.Replace("\n", "\r\n", StringComparison.Ordinal);
        var gitignore = PythonToolCacheFixture.Gitignore(tool);
        var swappedGitignore = gitignore.Contains("\r\n", StringComparison.Ordinal) ? gitignore.Replace("\r\n", "\n", StringComparison.Ordinal) : gitignore.Replace("\n", "\r\n", StringComparison.Ordinal);
        var cache = PythonToolCacheFixture.Create(Path.Combine(ApproveRoot(), "project"), tool, swapped, swappedGitignore);

        Assert.Equal([cache], (await ProviderFor(tool).PlanAsync()).TargetedPaths);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task AFolderWithNoTagIsNotRecognised(PythonToolCache tool) =>
        await AssertDeclined(tool, tag: null, PythonToolCacheFixture.Gitignore(tool));

    /// <summary>
    /// The specification's header is the first 43 bytes, exactly. A file called <c>CACHEDIR.TAG</c>
    /// whose signature differs by one digit is not a tag.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public async Task ATagWithAWrongHeaderIsNotRecognised(PythonToolCache tool) =>
        await AssertDeclined(
            tool,
            PythonToolCacheFixture.Tag(tool).Replace(PythonToolCacheFixture.Header, "Signature: 8a477f597d28d172789f06886806bc56", StringComparison.Ordinal),
            PythonToolCacheFixture.Gitignore(tool));

    /// <summary>
    /// Nothing may come before the header, a byte order mark included, so a tag written by an editor
    /// that adds one is not a tag.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public async Task ATagOpeningWithAByteOrderMarkIsNotRecognised(PythonToolCache tool) =>
        await AssertDeclined(tool, "\uFEFF" + PythonToolCacheFixture.Tag(tool), PythonToolCacheFixture.Gitignore(tool));

    /// <summary>
    /// The case the declared table exists for: a tag somebody wrote by hand to keep a folder out of a
    /// backup carries the header and nothing that names the tool. Where the tool names itself in the
    /// tag, a bare one is not the tool's. Where it does not, the bare tag is accepted only beside the
    /// <c>.gitignore</c> the tool writes with it, so that one is taken away instead.
    /// </summary>
    [Theory]
    [InlineData(PythonToolCache.Pytest)]
    [InlineData(PythonToolCache.Mypy)]
    [InlineData(PythonToolCache.Tox)]
    public async Task ABareTagIsNotATagThisToolWrote(PythonToolCache tool) =>
        await AssertDeclined(tool, PythonToolCacheFixture.Header + "\n", PythonToolCacheFixture.Gitignore(tool));

    [Theory]
    [InlineData(PythonToolCache.Ruff)]
    [InlineData(PythonToolCache.Nox)]
    public async Task ABareTagWithoutTheToolsGitignoreIsNotRecognised(PythonToolCache tool)
    {
        await AssertDeclined(tool, PythonToolCacheFixture.Tag(tool), gitignore: null);
        await AssertDeclined(tool, PythonToolCacheFixture.Tag(tool), "# my own rules\n*.log\n", project: "edited");
    }

    /// <summary>
    /// Where the tool's tag is the bare header, a tag carrying more is somebody else's, even with the
    /// tool's <c>.gitignore</c> beside it.
    /// </summary>
    [Theory]
    [InlineData(PythonToolCache.Ruff)]
    [InlineData(PythonToolCache.Nox)]
    public async Task ATagCarryingAnotherToolsLineIsNotRecognisedWhereTheToolWritesItBare(PythonToolCache tool) =>
        await AssertDeclined(
            tool,
            PythonToolCacheFixture.Header + "\n# This file is a cache directory tag created by cargo.\n",
            PythonToolCacheFixture.Gitignore(tool));

    /// <summary>One tool's tag in another tool's folder names the wrong tool.</summary>
    [Fact]
    public async Task AnotherToolsTagIsNotThisToolsTag() =>
        await AssertDeclined(
            PythonToolCache.Pytest,
            PythonToolCacheFixture.Tag(PythonToolCache.Tox),
            PythonToolCacheFixture.Gitignore(PythonToolCache.Pytest));

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task AFolderWithNoConfigurationBesideItIsNotRecognised(PythonToolCache tool)
    {
        var cache = PythonToolCacheFixture.Create(
            Path.Combine(ApproveRoot(), "project"), tool, PythonToolCacheFixture.Tag(tool), PythonToolCacheFixture.Gitignore(tool), writeManifest: false);

        var plan = await ProviderFor(tool).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == cache);
    }

    /// <summary>
    /// §5.6, against the disk. The project, its configuration, its source, and a folder of the same
    /// name declined beside it all survive the removal of the recognised one.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public async Task TheProjectAndADeclinedNeighbourSurviveTheRemoval(PythonToolCache tool)
    {
        var root = ApproveRoot();
        var project = Path.Combine(root, "project");
        var cache = PythonToolCacheFixture.Create(project, tool);
        var declined = PythonToolCacheFixture.Create(Path.Combine(root, "handmade"), tool, tag: PythonToolCacheFixture.Header + "\n# kept out of my backups\n", gitignore: null);
        var manifest = Path.Combine(project, PythonToolCacheFixture.Manifest(tool));

        var provider = ProviderFor(tool);
        var plan = await provider.PlanAsync();

        Assert.Equal([cache], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == project && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == manifest && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == declined && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.DirectoryExists(cache));
        Assert.True(LongPath.FileExists(manifest));
        Assert.True(LongPath.FileExists(Path.Combine(project, "src", "module.py")));
        Assert.True(LongPath.DirectoryExists(declined));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task CleanedPlacesCoverEveryFolderThePlanRemoves(PythonToolCache tool)
    {
        PythonToolCacheFixture.Create(Path.Combine(ApproveRoot(), "team", "project"), tool);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(ProviderFor(tool)));
    }

    [Fact]
    public void EveryToolIsShipped()
    {
        using var shipped = new ShippedProviders();

        foreach (var tool in Enum.GetValues<PythonToolCache>())
        {
            Assert.Single(shipped.Providers, p => p.GetType() == ProviderFor(tool).GetType());
        }
    }

    private async Task AssertDeclined(PythonToolCache tool, string? tag, string? gitignore, string project = "project")
    {
        var cache = PythonToolCacheFixture.Create(Path.Combine(ApproveRoot(), project), tool, tag, gitignore);

        var plan = await ProviderFor(tool).PlanAsync();

        Assert.DoesNotContain(cache, plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == cache);
        Assert.True(LongPath.DirectoryExists(cache));
    }

    private string ApproveRoot()
    {
        var root = Path.Combine(_temp.Path, "src");
        Directory.CreateDirectory(root);
        _roots.Save([new SourceRoot(root)]);
        return root;
    }

    private BuildDirectoryProvider ProviderFor(PythonToolCache tool)
    {
        var scanner = new FakeDirectoryScanner();
        var discovery = new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory());
        var runner = new FakeProcessRunner();
        var live = FakeLiveTreeInspector.NothingLive;
        var inspector = FakeProcessInspector.NothingRunning;

        return tool switch
        {
            PythonToolCache.Pytest => new PytestCacheProvider(_roots, discovery, live, _environment, runner, inspector, scanner),
            PythonToolCache.Mypy => new MypyCacheProvider(_roots, discovery, live, _environment, runner, inspector, scanner),
            PythonToolCache.Ruff => new RuffCacheProvider(_roots, discovery, live, _environment, runner, inspector, scanner),
            PythonToolCache.Tox => new ToxEnvironmentProvider(_roots, discovery, live, _environment, runner, inspector, scanner),
            PythonToolCache.Nox => new NoxEnvironmentProvider(_roots, discovery, live, _environment, runner, inspector, scanner),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
        };
    }
}
