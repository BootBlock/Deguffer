using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The task cache Nx keeps in a workspace's <c>.nx</c> folder, and nothing else in that folder: the
/// index and daemon logs in <c>workspace-data</c> and the Nx wrapper's committed files stay.
/// </summary>
public sealed class NxWorkspaceCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;

    public NxWorkspaceCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private string Git => _environment.FindExecutable("git")!;

    [Fact]
    public async Task OffersTheCacheOfAWorkspaceAtTierOne()
    {
        var workspace = CreateWorkspace(Path.Combine(ApproveRoot(), "shop"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal([workspace.Nx.Cache], plan.TargetedPaths);
        Assert.Contains(plan.Steps.OfType<DeleteStep>(), step => step.What == "Nx task cache for shop");
        Assert.True(plan.EstimatedBytes > 0);
    }

    /// <summary>
    /// §5.6's negative, on the case it exists for: everything in <c>.nx</c> beside the cache is Nx's
    /// own or the workspace's, and a rule reaching one folder too far would take it.
    /// </summary>
    [Fact]
    public async Task RemovesTheCacheAndProvesTheRestOfTheWorkspaceSurvived()
    {
        var workspace = CreateWorkspace(Path.Combine(ApproveRoot(), "shop"));
        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Each sibling of the cache is named, and asserting it survived covers what is inside it.
        foreach (var sibling in workspace.Kept.Where(kept => Path.GetDirectoryName(kept) == workspace.Nx.Folder))
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(workspace.Folder, "nx.json"));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == workspace.Folder);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == workspace.Nx.Folder);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);

        Assert.False(Directory.Exists(workspace.Nx.Cache));
        Assert.All(workspace.Kept, kept => Assert.True(LongPath.EntryExists(kept), $"{kept} was removed"));
        Assert.All(workspace.Nx.Databases, database => Assert.True(File.Exists(database)));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// §5.2's unrecognised case. A folder called <c>.nx</c> with no <c>nx.json</c> beside it is not
    /// known to be Nx's, whatever it holds, so the whole of it is left alone.
    /// </summary>
    [Fact]
    public async Task AFolderCalledNxWithNoWorkspaceBesideItIsLeftAloneWhole()
    {
        var workspace = CreateWorkspace(Path.Combine(ApproveRoot(), "shop"));
        File.Delete(Path.Combine(workspace.Folder, "nx.json"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == workspace.Nx.Folder);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not be confirmed as an Nx workspace's .nx folder", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(workspace.Nx.Entry));
    }

    /// <summary>
    /// Nx 19 to 22 name the index without a version, and 23.0 to 23.2 with <c>-v3</c>. Either reports
    /// a removed task as restored and restores nothing, so a cache it indexes stays, and the plan says
    /// what to run instead. A <c>cache</c> without Nx's <c>terminalOutputs</c> is not known to be Nx's.
    /// </summary>
    [Theory]
    [InlineData("unversioned-index")]
    [InlineData("v3-index")]
    [InlineData("no-terminal-outputs")]
    public async Task ACacheThatCannotBeShownSafeIsLeftAlone(string broken)
    {
        var workspace = broken switch
        {
            "unversioned-index" => CreateWorkspace(Path.Combine(ApproveRoot(), "shop"), ["4d1c7e0b-machine.db"]),
            "v3-index" => CreateWorkspace(Path.Combine(ApproveRoot(), "shop"), ["4d1c7e0b-machine-v3.db"]),
            _ => CreateWorkspace(Path.Combine(ApproveRoot(), "shop")),
        };

        if (broken == "no-terminal-outputs")
        {
            Directory.Delete(Path.Combine(workspace.Nx.Cache, "terminalOutputs"), recursive: true);
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == workspace.Nx.Cache);
        Assert.Contains(plan.Notes, n => n.Message.StartsWith($"Leaving '{workspace.Nx.Cache}' alone", StringComparison.Ordinal));
        Assert.True(plan.WasNotExamined);

        if (broken != "no-terminal-outputs")
        {
            Assert.Contains(plan.Notes, n => n.Message.Contains("Run 'nx reset' in the workspace", StringComparison.Ordinal));
        }

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(workspace.Nx.Entry));
    }

    /// <summary>Nx 17 and 18 kept no index at all, so nothing names a removed entry, and the cache is offered.</summary>
    [Fact]
    public async Task ACacheWithNoIndexIsOffered()
    {
        var workspace = CreateWorkspace(Path.Combine(ApproveRoot(), "shop"), databases: []);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([workspace.Nx.Cache], plan.TargetedPaths);
    }

    /// <summary>
    /// The veto asks about the workspace, where a run and the daemon work, and the index's files, which
    /// they hold open.
    /// </summary>
    [Fact]
    public async Task TheVetoIsAskedAboutTheWorkspaceAndTheIndexFiles()
    {
        var workspace = CreateWorkspace(Path.Combine(ApproveRoot(), "shop"));
        var inspector = FakeLiveTreeInspector.NothingLive;

        await CreateProvider(live: inspector).PlanAsync();

        var asked = Assert.Single(inspector.Asked);
        Assert.Equal(workspace.Nx.Folder, asked.Directory);
        Assert.Equal(workspace.Folder, asked.Project);
        Assert.Equal([.. workspace.Nx.Databases.Order(StringComparer.OrdinalIgnoreCase)], [.. asked.LockFileNames.Order(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>A workspace Nx starts working in between the preview and the clean keeps its cache.</summary>
    [Fact]
    public async Task AWorkspaceNxStartsInAfterThePreviewKeepsItsCache()
    {
        var root = ApproveRoot();
        var busy = CreateWorkspace(Path.Combine(root, "busy"));
        var idle = CreateWorkspace(Path.Combine(root, "idle"));
        var live = FakeLiveTreeInspector.NothingLive;

        var provider = CreateProvider(live: live);
        var plan = await provider.PlanAsync();

        Assert.Equal(2, plan.TargetedPaths.Count);

        live.WithProgram("node", workingDirectory: busy.Folder);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(busy.Nx.Entry), "the cache was removed under a running Nx");
        Assert.False(Directory.Exists(idle.Nx.Cache));
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: node is working in busy.");
    }

    /// <summary>
    /// A cache git tracks a file in is somebody's fixture. The question is asked of the cache alone,
    /// because the Nx wrapper's files beside it are committed on purpose.
    /// </summary>
    [Fact]
    public async Task ACacheGitTracksAFileInIsLeftAloneAndTheWrappersFilesDoNotCount()
    {
        var repository = Path.Combine(ApproveRoot(), "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var committed = CreateWorkspace(Path.Combine(repository, "fixtures", "committed"));
        var ignored = CreateWorkspace(Path.Combine(repository, "app"));

        _environment.WithExecutable("git");
        var runner = new FakeProcessRunner().Responding(Git, "ls-files", "fixtures/committed/.nx/cache/terminalOutputs/1\0app/.nx/nxw.js\0");

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Equal([ignored.Nx.Cache], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(committed.Nx.Cache, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("tracked in git", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithNoSourceFolderItOffersNothingAndSaysWhatItNeeds()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True(provider.IsAwaitingSourceFolders);
        Assert.Empty((await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>Explore refuses a <c>.nx</c> folder the plan holds back as in use (§7.1).</summary>
    [Fact]
    public async Task ExploreRefusesANxFolderSomethingIsUsing()
    {
        var root = ApproveRoot();
        var busy = CreateWorkspace(Path.Combine(root, "busy"));
        CreateWorkspace(Path.Combine(root, "idle"));

        var provider = CreateProvider(live: FakeLiveTreeInspector.NothingLive.WithProgram("node", workingDirectory: busy.Folder));

        var declared = Assert.Single(await provider.DiscoverToolRootsAsync());
        Assert.Equal(busy.Nx.Folder, declared.Path);
    }

    private string ApproveRoot()
    {
        var root = Path.Combine(_temp.Path, "src");
        Directory.CreateDirectory(root);
        _roots.Save([new SourceRoot(root)]);
        return root;
    }

    /// <summary>
    /// A workspace as Nx 23.1 leaves one, with a current index unless <paramref name="databases"/> says otherwise: <c>nx.json</c>, and in <c>.nx</c> the cache, the index with
    /// the daemon's log beside it, and the wrapper's committed files.
    /// </summary>
    private static NxWorkspace CreateWorkspace(string folder, string[]? databases = null)
    {
        NxCacheFixture.Write(Path.Combine(folder, "nx.json"), "{ \"targetDefaults\": {} }");
        NxCacheFixture.Write(Path.Combine(folder, "package.json"), "{}");

        var nx = databases is null
            ? NxCacheFixture.CreateCurrent(Path.Combine(folder, ".nx"), "workspace-data")
            : NxCacheFixture.Create(Path.Combine(folder, ".nx"), "workspace-data", databases);

        var daemonLog = Path.Combine(nx.Index, "d", "daemon.log");
        var wrapper = Path.Combine(nx.Folder, "nxw.js");
        var installation = Path.Combine(nx.Folder, "installation", "package.json");
        NxCacheFixture.Write(daemonLog, "started");
        NxCacheFixture.Write(wrapper, "// wrapper");
        NxCacheFixture.Write(installation, "{}");

        return new NxWorkspace(folder, nx, [nx.Index, daemonLog, wrapper, Path.Combine(nx.Folder, "installation"), installation, Path.Combine(folder, "nx.json"), Path.Combine(folder, "package.json")]);
    }

    private NxWorkspaceCacheProvider CreateProvider(FakeProcessRunner? runner = null, FakeLiveTreeInspector? live = null)
    {
        var scanner = new FakeDirectoryScanner();

        return new NxWorkspaceCacheProvider(
            _roots,
            new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()),
            live ?? FakeLiveTreeInspector.NothingLive,
            _environment,
            runner ?? new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner);
    }

    private sealed record NxWorkspace(string Folder, NxCache Nx, IReadOnlyList<string> Kept);
}
