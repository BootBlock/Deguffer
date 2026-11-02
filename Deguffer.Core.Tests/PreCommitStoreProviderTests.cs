using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// pre-commit's store holds the unstaged changes it stashed during a commit beside the hook
/// environments, which is why only <c>pre-commit gc</c> is used and why most of these are about what
/// survives. No pre-commit was installed on the machine these were written on, so the layout is the
/// one <c>store.py</c> and <c>staged_files_only.py</c> write.
/// </summary>
public sealed class PreCommitStoreProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public PreCommitStoreProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private string Store => Path.Combine(_environment.UserProfile, ".cache", "pre-commit");

    private string PreCommit => _environment.FindExecutable("pre-commit")!;

    private PreCommitStoreProvider CreateProvider(FakeProcessRunner? runner = null, IProcessInspector? inspector = null) =>
        new(_environment, runner ?? new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    private static string CreateFolder(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "payload.bin"), new byte[bytes]);
        return path;
    }

    private static string CreateFile(string path, int bytes = 512)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// What a pre-commit that has installed two hook repositories and stashed changes once leaves in
    /// <paramref name="store"/>, plus a folder and a file pre-commit never writes.
    /// </summary>
    private static (IReadOnlyList<string> Repositories, IReadOnlyList<string> Kept) CreateLayout(string store)
    {
        List<string> repositories =
        [
            CreateFolder(Path.Combine(store, "repoab12cd_3"), 8192),
            CreateFolder(Path.Combine(store, "repo0z9y8x7w"), 4096),
        ];

        List<string> kept =
        [
            CreateFile(Path.Combine(store, "db.db")),
            CreateFile(Path.Combine(store, ".lock"), 0),
            CreateFile(Path.Combine(store, "README")),
            CreateFile(Path.Combine(store, "patch1761000000-4242"), 65536),
            CreateFolder(Path.Combine(store, "repository-notes")),
            CreateFile(Path.Combine(store, "tmpq1w2e3r4")),
        ];

        return (repositories, kept);
    }

    [Fact]
    public async Task ReportsNotPresentWhenPreCommitHasNeverRun()
    {
        _environment.WithExecutable("pre-commit");
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>A store holding only stashed changes has nothing <c>gc</c> could remove, so the row is not shown.</summary>
    [Fact]
    public async Task IsNotPresentWhereTheStoreHoldsNoHookRepository()
    {
        _environment.WithExecutable("pre-commit");
        CreateFile(Path.Combine(Store, "db.db"));
        CreateFile(Path.Combine(Store, "patch1761000000-4242"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("no hook environments", StringComparison.Ordinal));
    }

    /// <summary>
    /// Deciding which repositories no project uses is <c>gc</c>'s job, so without the command the row is
    /// shown, offers nothing, and is not "already clear".
    /// </summary>
    [Fact]
    public async Task WithoutTheCommandTheStoreIsReportedAndNothingIsOffered()
    {
        CreateLayout(Store);
        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("holds 2 hook repositories", StringComparison.Ordinal)
            && n.Message.Contains("no 'pre-commit' command is on the PATH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvictsWithGcAndNeverCleanOrAPath()
    {
        _environment.WithExecutable("pre-commit");
        CreateLayout(Store);

        var plan = await CreateProvider().PlanAsync();

        // §5.1: nothing is targeted for deletion, and the tool's own gc decides what goes.
        Assert.Empty(plan.TargetedPaths);
        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(PreCommit, step.FileName);
        Assert.Equal("gc", step.Arguments);
        Assert.Equal(CancelledCommand.Finishes, step.WhenCancelled);
    }

    /// <summary>
    /// <c>gc</c> has no dry run and keeps what a project still uses, so the figure is every repository
    /// folder and says it is a ceiling. The stashed changes and the database are not part of it.
    /// </summary>
    [Fact]
    public async Task TheEstimateIsACeilingOverTheRepositoryFoldersOnly()
    {
        _environment.WithExecutable("pre-commit");
        var (repositories, _) = CreateLayout(Store);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.True(step.Estimated.IsCeiling);
        Assert.Equal(8192 + 4096, step.EstimatedBytes);
        Assert.Equal(
            repositories.Order(StringComparer.OrdinalIgnoreCase),
            step.MeasuredPaths.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// §5.6: the store, pre-commit's own files, every stashed change and everything pre-commit never
    /// writes are named survivors. The repository folders are not, because <c>gc</c> may remove them.
    /// </summary>
    [Fact]
    public async Task TheStoreItsRecordAndTheStashedChangesAreSurvivors()
    {
        _environment.WithExecutable("pre-commit");
        var (repositories, kept) = CreateLayout(Store);

        var plan = await CreateProvider().PlanAsync();

        foreach (var path in kept.Append(Store))
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.DoesNotContain(plan.ProtectedPaths, p => repositories.Contains(p.Path, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.EndsWith("patch1761000000-4242", StringComparison.Ordinal)
            && p.Reason.Contains("only copy of that work", StringComparison.Ordinal));
    }

    /// <summary>A run in which <c>gc</c> removed one unused repository verifies clean.</summary>
    [Fact]
    public async Task ARunInWhichGcRemovesARepositoryPasses()
    {
        _environment.WithExecutable("pre-commit");
        var (repositories, kept) = CreateLayout(Store);
        var runner = new FakeProcessRunner().Replying(PreCommit, arguments =>
        {
            Directory.Delete(repositories[0], recursive: true);
            return new CommandOutcome(0, "1 repo(s) removed.", string.Empty);
        });

        var provider = CreateProvider(runner);
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.Equal("gc", Assert.Single(runner.Invocations).Arguments);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.All(kept, path => Assert.True(Path.Exists(path), path));
    }

    /// <summary>
    /// The over-broad command §5.6 exists to catch: a <c>clean</c>, or a <c>gc</c> that took a stashed
    /// change with it, fails the run.
    /// </summary>
    [Fact]
    public async Task VerificationFailsLoudlyIfAStashedChangeVanished()
    {
        _environment.WithExecutable("pre-commit");
        CreateLayout(Store);
        var patch = Path.Combine(Store, "patch1761000000-4242");
        var runner = new FakeProcessRunner().Replying(PreCommit, arguments =>
        {
            File.Delete(patch);
            return new CommandOutcome(0, "0 repo(s) removed.", string.Empty);
        });

        var provider = CreateProvider(runner);
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(result.Verification!.Passed);
        Assert.Contains(result.Verification.Failures, f => f.Subject.Equals(patch, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FollowsPreCommitHome()
    {
        _environment.WithExecutable("pre-commit");
        var moved = Path.Combine(_temp.Path, "hooks");
        var (repositories, _) = CreateLayout(moved);
        CreateLayout(Store);
        _environment.WithEnvironmentVariable(PreCommitStoreProvider.HomeVariable, moved);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(repositories.Count, step.MeasuredPaths.Count);
        Assert.All(step.MeasuredPaths, path => Assert.StartsWith(moved, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Without <c>PRE_COMMIT_HOME</c>, pre-commit keeps its store in a <c>pre-commit</c> folder of the cache home.</summary>
    [Fact]
    public async Task FollowsTheCacheHomeIntoItsPreCommitFolder()
    {
        _environment.WithExecutable("pre-commit");
        var home = Path.Combine(_temp.Path, "xdg");
        CreateLayout(Path.Combine(home, "pre-commit"));
        _environment.WithEnvironmentVariable(PreCommitStoreProvider.XdgCacheVariable, home);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.All(step.MeasuredPaths, path =>
            Assert.StartsWith(Path.Combine(home, "pre-commit"), path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>pre-commit reads <c>PRE_COMMIT_HOME or ...</c>, so an empty value counts as unset and the cache home stands.</summary>
    [Fact]
    public void AnEmptyPreCommitHomeFallsThroughToTheCacheHome()
    {
        _environment.WithExecutable("pre-commit");
        var home = Path.Combine(_temp.Path, "xdg");
        CreateLayout(Path.Combine(home, "pre-commit"));
        _environment.WithEnvironmentVariable(PreCommitStoreProvider.HomeVariable, "  ");
        _environment.WithEnvironmentVariable(PreCommitStoreProvider.XdgCacheVariable, home);

        Assert.Equal(Path.Combine(home, "pre-commit"), CreateProvider().ResolveStore(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>gc</c> acts on the store its own environment names, so a variable Deguffer started with and the
    /// machine has since dropped still decides which store is examined.
    /// </summary>
    [Fact]
    public async Task ExaminesTheStoreTheCommandWillActOn()
    {
        _environment.WithExecutable("pre-commit");
        var moved = Path.Combine(_temp.Path, "hooks");
        CreateLayout(moved);
        CreateLayout(Store);
        _environment.WithToolEnvironmentVariable(PreCommitStoreProvider.HomeVariable, moved);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.All(step.MeasuredPaths, path => Assert.StartsWith(moved, path, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(moved, "db.db"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>pre-commit makes a relative value absolute against its own working directory, which Deguffer does not share.</summary>
    [Theory]
    [InlineData(PreCommitStoreProvider.HomeVariable)]
    [InlineData(PreCommitStoreProvider.XdgCacheVariable)]
    public async Task OffersNothingWhenTheSettingIsRelative(string variable)
    {
        _environment.WithExecutable("pre-commit");
        CreateLayout(Store);
        _environment.WithEnvironmentVariable(variable, @"..\cache");

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.ToolRoots);

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a full path", StringComparison.Ordinal)
            && n.Message.StartsWith($@"{variable} is set to '..\cache'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>PRE_COMMIT_HOME</c> naming the profile is declined, and Explore still refuses pre-commit's own
    /// files there and nothing else.
    /// </summary>
    [Fact]
    public async Task ADeclinedFolderStillRefusesPreCommitsRecord()
    {
        _environment.WithExecutable("pre-commit");
        _environment.WithEnvironmentVariable(PreCommitStoreProvider.HomeVariable, _environment.UserProfile);

        var provider = CreateProvider();

        Assert.Empty(provider.ToolRoots);
        Assert.True((await provider.PlanAsync()).IsEmpty);

        var root = Assert.Single(await provider.DiscoverToolRootsAsync());
        Assert.Equal(_environment.UserProfile, root.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ToolRootClaim.NamedEntries, root.Claim);
        Assert.False(root.Recognises(new ToolRootChild("db.db", ChildKind.File)));
        Assert.True(root.RecognisesFolder("Documents"));
    }

    /// <summary>
    /// §5.2 for Explore: a repository folder removed by hand leaves its row in <c>db.db</c> and breaks
    /// every project using it, so the store recognises nothing, a repository folder included.
    /// </summary>
    [Fact]
    public void TheToolRootRecognisesNothingInTheStore()
    {
        var root = Assert.Single(CreateProvider().ToolRoots);

        Assert.Equal(Store, root.Path, StringComparer.OrdinalIgnoreCase);
        Assert.False(root.RecognisesFolder("repoab12cd_3"));
        Assert.False(root.Recognises(new ToolRootChild("patch1761000000-4242", ChildKind.File)));
        Assert.False(root.Recognises(new ToolRootChild("db.db", ChildKind.File)));
    }

    /// <summary>Only <c>repo</c> and Python's eight random characters is a folder <c>mkdtemp</c> made.</summary>
    [Theory]
    [InlineData("repo")]
    [InlineData("repoab12cd3")]
    [InlineData("repoab12cd_34")]
    [InlineData("repoAB12CD_9")]
    [InlineData("repo-ab12cd3")]
    public async Task AFolderNotNamedAsMkdtempNamesItIsNeitherMeasuredNorTaken(string name)
    {
        _environment.WithExecutable("pre-commit");
        CreateFolder(Path.Combine(Store, "repoab12cd_3"));
        var other = CreateFolder(Path.Combine(Store, name));

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.DoesNotContain(other, step.MeasuredPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(other, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LeavesALinkedStoreAlone()
    {
        _environment.WithExecutable("pre-commit");
        var target = Path.Combine(_temp.Path, "elsewhere");
        CreateLayout(target);
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        Junction.ToDirectory(Store, target);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
    }

    [Fact]
    public async Task SaysHowManyStashedChangesItLeaves()
    {
        _environment.WithExecutable("pre-commit");
        CreateLayout(Store);
        CreateFile(Path.Combine(Store, "patch1761000100-4343"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains("holds 2 copies of unstaged changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WarnsWhenPreCommitIsRunning()
    {
        _environment.WithExecutable("pre-commit");
        CreateLayout(Store);

        var plan = await CreateProvider(inspector: new FakeProcessInspector("pre-commit")).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("pre-commit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        _environment.WithExecutable("pre-commit");
        CreateLayout(Store);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>A plan holds display paths; the I/O beneath it extends them where it acts (§6.3).</summary>
    [Fact]
    public async Task ThePlanHoldsDisplayPaths()
    {
        _environment.WithExecutable("pre-commit");
        CreateLayout(Store);

        var plan = await CreateProvider().PlanAsync();

        var paths = plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths)
            .Concat(plan.ProtectedPaths.Select(p => p.Path))
            .ToList();
        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.False(path.StartsWith(@"\\?\", StringComparison.Ordinal), path));
    }
}
