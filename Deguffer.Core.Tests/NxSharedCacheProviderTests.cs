using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The task caches Nx keeps in the profile, one per workspace under <c>~\.nx\&lt;id&gt;</c>. The layout
/// is Nx 24.0.3's, from its source: a <c>cache</c> and a <c>databases</c> folder in each workspace's
/// folder, and nothing else Nx makes there.
/// </summary>
public sealed class NxSharedCacheProviderTests : IDisposable
{
    private const string Workspace = "0f3a9c27d4e8b615";
    private const string OtherWorkspace = "a1b2c3d4e5f60718";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public NxSharedCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string Root => Path.Combine(_environment.UserProfile, ".nx");

    [Fact]
    public async Task OffersEachWorkspacesCacheAtTierOneAndNothingElse()
    {
        var first = Create(Workspace);
        var second = Create(OtherWorkspace);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal(
            [first.Cache, second.Cache],
            [.. plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase)]);
        Assert.All(plan.Steps, step => Assert.IsType<DeleteDirectoryStep>(step));
        Assert.Contains(plan.Steps.OfType<DeleteStep>(), step => step.What == $"Nx task cache for workspace {Workspace}");
        Assert.True(plan.EstimatedBytes > 0);
    }

    /// <summary>
    /// §5.6's negative: the root, each workspace's folder, its index and anything else in either survive,
    /// and are asserted to, because each is a sibling or a parent of a removed cache.
    /// </summary>
    [Fact]
    public async Task RemovesTheCacheAndProvesTheIndexTheFoldersAndTheUnrecognisedSurvived()
    {
        var nx = Create(Workspace);
        var stranger = Path.Combine(nx.Folder, "notes");
        NxCacheFixture.Write(Path.Combine(stranger, "keep.txt"), "mine");
        var unrelated = Path.Combine(Root, "nxw");
        NxCacheFixture.Write(Path.Combine(unrelated, "cache", "terminalOutputs", "1"), "not a workspace's folder");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([nx.Cache], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Root);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == nx.Folder);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == nx.Index);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == stranger);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == unrelated);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);

        Assert.False(Directory.Exists(nx.Cache));
        Assert.All(nx.Databases, database => Assert.True(File.Exists(database), $"{database} was removed"));
        Assert.True(File.Exists(Path.Combine(stranger, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(unrelated, "cache", "terminalOutputs", "1")));
        Assert.True(Directory.Exists(nx.Folder));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// The case the version gate exists for. Nx 19 to 23.2 read an index named without a version or
    /// with <c>-v3</c>, and would report a removed entry as restored and restore nothing. A current
    /// database beside an old one does not help, because the old Nx reads its own.
    /// </summary>
    [Theory]
    [InlineData("4d1c7e0b-machine-v3.db")]
    [InlineData("4d1c7e0b-machine.db")]
    [InlineData("4d1c7e0b-machine-v3.db-wal")]
    [InlineData("4d1c7e0b-machine-v6.db", "4d1c7e0b-machine-v3.db")]
    public async Task ACacheAnOlderNxIndexesIsLeftAlone(params string[] databases)
    {
        var old = NxCacheFixture.Create(Path.Combine(Root, Workspace), "databases", databases);
        var current = Create(OtherWorkspace);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([current.Cache], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == old.Cache && p.Reason.Contains("Nx 23.2 or earlier", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.Contains($"Leaving '{old.Cache}' alone", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(old.Entry));
    }

    /// <summary>Every schema from the one that added the check is current, and so is no index at all.</summary>
    [Theory]
    [InlineData("4d1c7e0b-machine-v4.db")]
    [InlineData("4d1c7e0b-machine-v12.db")]
    [InlineData]
    public async Task ACacheACurrentNxIndexesOrNothingIndexesIsOffered(params string[] databases)
    {
        var nx = NxCacheFixture.Create(Path.Combine(Root, Workspace), "databases", databases);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([nx.Cache], plan.TargetedPaths);
    }

    /// <summary>
    /// §5.2's unrecognised case. A name is not evidence: a <c>cache</c> without the <c>terminalOutputs</c>
    /// Nx makes in every cache, and a folder in <c>~\.nx</c> not named as Nx names a workspace's, are
    /// somebody else's, whatever they hold.
    /// </summary>
    [Theory]
    [InlineData("no-terminal-outputs")]
    [InlineData("terminal-outputs-is-a-file")]
    [InlineData("upper-case-id")]
    [InlineData("short-id")]
    public async Task AFolderNxDidNotMakeIsLeftAlone(string broken)
    {
        var id = broken switch
        {
            "upper-case-id" => Workspace.ToUpperInvariant(),
            "short-id" => Workspace[..15],
            _ => Workspace,
        };

        var nx = Create(id);
        var terminalOutputs = Path.Combine(nx.Cache, "terminalOutputs");

        if (broken is "no-terminal-outputs" or "terminal-outputs-is-a-file")
        {
            Directory.Delete(terminalOutputs, recursive: true);
        }

        if (broken == "terminal-outputs-is-a-file")
        {
            File.WriteAllText(terminalOutputs, "a file");
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => LongPath.Contains(p.Path, nx.Cache));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(nx.Entry));
    }

    /// <summary>A cache folder that is a link is never followed, so what it points at is untouched.</summary>
    [Fact]
    public async Task ACacheThatIsALinkIsNotFollowed()
    {
        var elsewhere = NxCacheFixture.CreateCurrent(Path.Combine(_temp.Path, "elsewhere"), "databases");
        var folder = Path.Combine(Root, Workspace);
        NxCacheFixture.Write(Path.Combine(folder, "databases", NxCacheFixture.CurrentDatabase), "SQLite format 3\0");
        Junction.ToDirectory(Path.Combine(folder, "cache"), elsewhere.Cache);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(folder, "cache"));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(elsewhere.Entry));
    }

    /// <summary>
    /// Nx holds the index open while it runs a task and while its daemon runs, wherever the workspace
    /// is, so the in-use question is about those files: the database and its write-ahead log.
    /// </summary>
    [Fact]
    public async Task TheVetoIsAskedAboutTheIndexFiles()
    {
        var nx = Create(Workspace);
        var inspector = FakeLiveTreeInspector.NothingLive;

        await CreateProvider(inspector).PlanAsync();

        var asked = Assert.Single(inspector.Asked);
        Assert.Equal(nx.Cache, asked.Directory);
        Assert.Equal(nx.Folder, asked.Project);
        Assert.Equal([.. nx.Databases.Order(StringComparer.OrdinalIgnoreCase)], [.. asked.LockFileNames.Order(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>A workspace that starts running between the preview and the clean keeps its cache.</summary>
    [Fact]
    public async Task ACacheWhoseIndexIsOpenedAfterThePreviewIsKept()
    {
        var running = Create(Workspace);
        var idle = Create(OtherWorkspace);
        var live = FakeLiveTreeInspector.NothingLive;

        var provider = CreateProvider(live);
        var plan = await provider.PlanAsync();

        Assert.Equal(2, plan.TargetedPaths.Count);

        live.WithHeldFile(running.Databases[0], "node");

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(running.Entry), "the cache was removed while Nx held its index");
        Assert.False(Directory.Exists(idle.Cache));
    }

    /// <summary>"Could not tell" is not "nothing is using it", so an answer that is not whole offers nothing.</summary>
    [Fact]
    public async Task NothingIsOfferedWhenWhetherAnythingIsUsingItCannotBeTold()
    {
        var nx = Create(Workspace);

        var plan = await CreateProvider(FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == nx.Cache);
    }

    [Fact]
    public async Task WithoutNxsFolderItIsAbsentAndOffersNothing()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// §7.1: Explore may take a cache the plan offers, and never an index, a workspace's folder or the
    /// root. A cache the plan holds back as in use is refused whole.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesTheIndexAndACacheInUse()
    {
        var idle = Create(Workspace);
        var held = Create(OtherWorkspace);
        var live = FakeLiveTreeInspector.NothingLive.WithHeldFile(held.Databases[0], "node");

        var roots = await CreateProvider(live).DiscoverToolRootsAsync();

        var top = Assert.Single(roots, root => root.Path == Root);
        Assert.True(top.RecognisesFolder(Workspace));
        Assert.False(top.RecognisesFolder(OtherWorkspace));

        var workspace = Assert.Single(roots, root => root.Path == idle.Folder);
        Assert.True(workspace.RecognisesFolder("cache"));
        Assert.False(workspace.RecognisesFolder("databases"));

        var refused = Assert.Single(roots, root => root.Path == held.Cache);
        Assert.False(refused.RecognisesFolder(NxCacheFixture.Hash));
    }

    private NxCache Create(string id) => NxCacheFixture.CreateCurrent(Path.Combine(Root, id), "databases");

    private NxSharedCacheProvider CreateProvider(FakeLiveTreeInspector? live = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            new FakeDirectoryScanner(),
            live ?? FakeLiveTreeInspector.NothingLive);
}
