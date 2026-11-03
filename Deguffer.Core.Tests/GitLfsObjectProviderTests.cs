using System.Security.Cryptography;
using System.Text;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Old Git LFS objects, removed by Git LFS's own prune (§5.1) from repositories found in the approved
/// source folders. git is never run: each test answers for it as git-lfs 3.8.1 was observed to. The
/// negative assertions carry the weight, because the repository, its git directory and every object
/// the dry run did not name must all outlive the prune, and nothing in a git directory is ever a path
/// target (§5.2).
/// </summary>
public sealed class GitLfsObjectProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;
    private readonly string _root;

    /// <summary>What each git directory's configuration answers, by the forward-slash form git is given.</summary>
    private readonly Dictionary<string, string> _configuration = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each git directory's dry run answers, by the forward-slash form git is given.</summary>
    private readonly Dictionary<string, CommandOutcome> _dryRuns = new(StringComparer.OrdinalIgnoreCase);

    public GitLfsObjectProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("git");
        _roots = new SourceRootStore(_environment);
        _root = Path.Combine(_temp.Path, "src");
        Directory.CreateDirectory(_root);
        _roots.Save([new SourceRoot(_root)]);
    }

    public void Dispose() => _temp.Dispose();

    private string Git => _environment.FindExecutable("git")!;

    private static string Oid(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));

    private static string Forward(string path) => path.Replace('\\', '/');

    private static string ObjectFile(string gitDirectory, string oid) =>
        Path.Combine(gitDirectory, "lfs", "objects", oid[..2], oid[2..4], oid);

    /// <summary>A git directory as git leaves one, with a Git LFS store holding each of <paramref name="objects"/>.</summary>
    private static string CreateGitDirectory(string gitDirectory, params (string Oid, int Size)[] objects)
    {
        Directory.CreateDirectory(Path.Combine(gitDirectory, "objects"));
        Directory.CreateDirectory(Path.Combine(gitDirectory, "refs", "heads"));
        File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(gitDirectory, "config"), "[core]\n");
        File.WriteAllText(Path.Combine(gitDirectory, "index"), "index");
        Directory.CreateDirectory(Path.Combine(gitDirectory, "lfs", "objects"));

        foreach (var (oid, size) in objects)
        {
            var file = ObjectFile(gitDirectory, oid);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[size]);
        }

        return gitDirectory;
    }

    /// <summary>A repository checked out at <paramref name="name"/> below the approved folder.</summary>
    private static string CreateRepository(string root, string name, params (string Oid, int Size)[] objects)
    {
        var workTree = Path.Combine(root, name);
        Directory.CreateDirectory(workTree);
        File.WriteAllText(Path.Combine(workTree, "README.md"), "source");
        CreateGitDirectory(Path.Combine(workTree, ".git"), objects);
        return workTree;
    }

    /// <summary>The report git-lfs 3.8.1 prints for a dry run naming <paramref name="pruned"/>, with progress forced.</summary>
    private static string Report(int local, params string[] pruned)
    {
        var report = new StringBuilder($"{local} local objects, {local - pruned.Length} retained, done.\n");

        if (pruned.Length > 0)
        {
            report.Append($"{pruned.Length} files would be pruned (1 MB)".PadRight(80)).Append("\r\n");

            foreach (var oid in pruned)
            {
                report.Append($" * {oid} (1 KB)".PadRight(80)).Append("\r\n");
            }

            report.Append($" * {pruned[^1]} (1 KB), done.\n");
        }

        return report.ToString();
    }

    private void DryRunReports(string gitDirectory, string report, int exitCode = 0) =>
        _dryRuns[Forward(gitDirectory)] = new CommandOutcome(exitCode, exitCode == 0 ? report : string.Empty, exitCode == 0 ? string.Empty : report);

    /// <summary>
    /// git's answer to the configuration query, as <c>config -z --get-regexp</c> prints it and as the
    /// runner hands it over: line by line, each line ended with <see cref="Environment.NewLine"/>, so
    /// the line feed after each key arrives as a carriage return and a line feed, and one more ends the
    /// output.
    /// </summary>
    private void Configured(string gitDirectory, params (string Key, string Value)[] entries) =>
        _configuration[Forward(gitDirectory)] =
            string.Concat(entries.Select(entry => $"{entry.Key}\n{entry.Value}\0")).ReplaceLineEndings() + Environment.NewLine;

    /// <summary>
    /// git as the plan meets it: Git LFS installed, every configuration query answered from
    /// <see cref="_configuration"/> or with nothing set, and every dry run from <see cref="_dryRuns"/>.
    /// </summary>
    private FakeProcessRunner Runner(bool lfsInstalled = true) =>
        new FakeProcessRunner().Replying(Git, arguments =>
        {
            if (arguments == "lfs version")
            {
                return lfsInstalled
                    ? new CommandOutcome(0, "git-lfs/3.8.1 (GitHub; windows amd64; go 1.27.3; git a662a6db)\n", string.Empty)
                    : new CommandOutcome(1, string.Empty, "git: 'lfs' is not a git command. See 'git --help'.");
            }

            var gitDirectory = GitDirectoryIn(arguments);

            if (arguments.Contains(" config -z --get-regexp ", StringComparison.Ordinal))
            {
                return _configuration.TryGetValue(gitDirectory, out var set)
                    ? new CommandOutcome(0, set, string.Empty)
                    : new CommandOutcome(1, string.Empty, string.Empty);
            }

            if (arguments.Contains(" lfs prune --dry-run --verbose", StringComparison.Ordinal))
            {
                return _dryRuns.TryGetValue(gitDirectory, out var dryRun)
                    ? dryRun
                    : throw new InvalidOperationException($"No dry run is set for {gitDirectory}.");
            }

            throw new InvalidOperationException($"git was run with '{arguments}', which the plan has no reason to run.");
        });

    /// <summary>The git directory a command names, as it names it.</summary>
    private static string GitDirectoryIn(string arguments)
    {
        const string opening = "\"--git-dir=";
        var start = arguments.IndexOf(opening, StringComparison.Ordinal) + opening.Length;
        return arguments[start..arguments.IndexOf('"', start)];
    }

    private GitLfsObjectProvider CreateProvider(FakeProcessRunner runner, ILiveTreeInspector? liveTrees = null)
    {
        var scanner = new FakeDirectoryScanner();

        return new GitLfsObjectProvider(
            _roots,
            new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()),
            liveTrees ?? FakeLiveTreeInspector.NothingLive,
            _environment,
            runner,
            FakeProcessInspector.NothingRunning,
            scanner);
    }

    private static RunCommandStep TheStep(CleanupPlan plan) => Assert.Single(plan.Steps.OfType<RunCommandStep>());

    private static IEnumerable<string> DryRunsOf(FakeProcessRunner runner) =>
        runner.Invocations.Where(i => i.Arguments.Contains("--dry-run", StringComparison.Ordinal)).Select(i => GitDirectoryIn(i.Arguments));

    [Fact]
    public async Task OffersOnePruneForARepositoryMeasuredAsTheObjectsTheDryRunNamesAtTierTwo()
    {
        var (old, older, current) = (Oid("old"), Oid("older"), Oid("current"));
        var game = CreateRepository(_root, "game", (old, 3_000), (older, 5_000), (current, 7_000));
        var gitDirectory = Path.Combine(game, ".git");
        DryRunReports(gitDirectory, Report(3, old, older));

        var plan = await CreateProvider(Runner()).PlanAsync();

        var step = TheStep(plan);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(8_000, step.EstimatedBytes);
        Assert.Equal(15_000, step.MeasuredBefore?.Reclaimable);
        Assert.Equal([Path.Combine(gitDirectory, "lfs", "objects")], step.MeasuredPaths);
        Assert.Equal(Git, step.FileName);
        Assert.Equal(_root, step.Group);
        Assert.Equal([new ItemFacet("Repository", "game")], step.Facets);
        Assert.Equal(StepGrain.Items, CreateProvider(Runner()).Grain);
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>Git for Windows hides <c>.git</c>, and the source search must still find it.</summary>
    [Fact]
    public async Task FindsAHiddenGitDirectory()
    {
        var old = Oid("old");
        var game = CreateRepository(_root, "game", (old, 3_000));
        var gitDirectory = Path.Combine(game, ".git");
        File.SetAttributes(gitDirectory, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
        DryRunReports(gitDirectory, Report(1, old));

        Assert.Equal(3_000, TheStep(await CreateProvider(Runner()).PlanAsync()).EstimatedBytes);
    }

    /// <summary>
    /// The clean names the git directory, the work tree and the store, so nothing is left for git or
    /// git-lfs to resolve when it runs, asks the remote about every object first, and never opens a
    /// sign-in window. Without <c>--dry-run</c>, and only there.
    /// </summary>
    [Fact]
    public async Task ThePruneNamesTheRepositoryAndItsStoreAndVerifiesWithTheRemote()
    {
        var old = Oid("old");
        var game = CreateRepository(_root, "my game", (old, 3_000));
        var gitDirectory = Path.Combine(game, ".git");
        DryRunReports(gitDirectory, Report(1, old));

        var step = TheStep(await CreateProvider(Runner()).PlanAsync());

        Assert.Equal(
            $"\"--git-dir={Forward(gitDirectory)}\" \"--work-tree={Forward(game)}\" "
            + $"-c \"lfs.storage={Forward(Path.Combine(gitDirectory, "lfs"))}\" "
            + "-c credential.interactive=never lfs prune --verify-remote",
            step.Arguments);
        Assert.Equal(CancelledCommand.Ended, step.WhenCancelled);
    }

    /// <summary>The dry run is pinned exactly as the prune is, forces the progress that lists every object, and only reads.</summary>
    [Fact]
    public async Task TheDryRunIsPinnedToTheSameStoreAndIsAQuery()
    {
        var old = Oid("old");
        var game = CreateRepository(_root, "game", (old, 3_000));
        var gitDirectory = Path.Combine(game, ".git");
        DryRunReports(gitDirectory, Report(1, old));
        var runner = Runner();

        await CreateProvider(runner).PlanAsync();

        var dryRun = Assert.Single(runner.Invocations, i => i.Arguments.Contains("--dry-run", StringComparison.Ordinal));
        Assert.Equal(
            $"\"--git-dir={Forward(gitDirectory)}\" \"--work-tree={Forward(game)}\" "
            + $"-c \"lfs.storage={Forward(Path.Combine(gitDirectory, "lfs"))}\" "
            + "-c lfs.forceprogress=true lfs prune --dry-run --verbose",
            dryRun.Arguments);
        Assert.Equal(ToolRun.Query, dryRun.Run);
    }

    /// <summary>
    /// §6.3: an approved folder given in the extended form still reaches git in the form git reads,
    /// because git takes no <c>\\?\</c> path.
    /// </summary>
    [Fact]
    public async Task GitIsNeverGivenTheExtendedForm()
    {
        _roots.Save([new SourceRoot(LongPath.Extended(_root))]);
        var old = Oid("old");
        var game = CreateRepository(_root, "game", (old, 3_000));
        DryRunReports(Path.Combine(game, ".git"), Report(1, old));
        var runner = Runner();

        var step = TheStep(await CreateProvider(runner).PlanAsync());

        Assert.All(
            runner.Invocations.Select(i => i.Arguments).Append(step.Arguments),
            arguments => Assert.DoesNotContain("?", arguments, StringComparison.Ordinal));
        Assert.Contains($"\"--git-dir={Forward(Path.Combine(game, ".git"))}\"", step.Arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.6: the repository, its git directory and what git needs in it, and the store itself, are
    /// protected, and stood before the run; nothing is a path the plan deletes.
    /// </summary>
    [Fact]
    public async Task ProtectsTheRepositoryAroundTheStore()
    {
        var old = Oid("old");
        var game = CreateRepository(_root, "game", (old, 3_000), (Oid("current"), 1_000));
        var gitDirectory = Path.Combine(game, ".git");
        File.WriteAllText(Path.Combine(gitDirectory, "packed-refs"), "# pack-refs\n");
        DryRunReports(gitDirectory, Report(2, old));

        var plan = await CreateProvider(Runner()).PlanAsync();

        string[] survivors =
        [
            game, gitDirectory,
            .. new[] { "HEAD", "config", "objects", "refs", "index", "packed-refs", "lfs", Path.Combine("lfs", "objects") }
                .Select(name => Path.Combine(gitDirectory, name)),
        ];

        foreach (var survivor in survivors)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.Empty(plan.TargetedPaths);
    }

    [Fact]
    public async Task VerificationFailsIfTheRepositoryVanished()
    {
        var old = Oid("old");
        var game = CreateRepository(_root, "game", (old, 3_000));
        DryRunReports(Path.Combine(game, ".git"), Report(1, old));
        var provider = CreateProvider(Runner());
        var plan = await provider.PlanAsync();

        File.Delete(Path.Combine(game, ".git", "HEAD"));

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, f => f.Subject.Equals(Path.Combine(game, ".git", "HEAD"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>§5.2: a folder named <c>.git</c> with no <c>HEAD</c> is not a git directory, so git is never asked about it.</summary>
    [Fact]
    public async Task DeclinesAFolderNamedDotGitThatIsNotAGitDirectory()
    {
        var impostor = Path.Combine(_root, "notes", ".git");
        Directory.CreateDirectory(Path.Combine(impostor, "lfs", "objects", "ab", "cd"));
        File.WriteAllBytes(Path.Combine(impostor, "lfs", "objects", "ab", "cd", Oid("x")), new byte[100]);
        var runner = Runner();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(runner.Invocations);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(impostor, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not be confirmed as a git directory", StringComparison.Ordinal));
    }

    /// <summary>A repository Git LFS has never stored an object for costs no process at all.</summary>
    [Fact]
    public async Task RunsNothingForARepositoryWithoutAStore()
    {
        var plain = CreateRepository(_root, "plain");
        Directory.Delete(Path.Combine(plain, ".git", "lfs"), recursive: true);
        var runner = Runner();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
        Assert.Empty(runner.Invocations);
        Assert.Contains(plan.Notes, n => n.Message == "No repository in your source folders keeps files for Git LFS.");
    }

    /// <summary>A store whose dry run names nothing is offered as nothing, and is not a row that was not examined.</summary>
    [Fact]
    public async Task OffersNothingWhereEveryObjectIsKept()
    {
        var game = CreateRepository(_root, "game", (Oid("current"), 1_000));
        DryRunReports(Path.Combine(game, ".git"), Report(1));

        var plan = await CreateProvider(Runner()).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
    }

    /// <summary>
    /// The manual: never prune a store several repositories may share. A repository whose
    /// <c>lfs.storage</c> is set elsewhere is not pruned, and git-lfs is not asked about it.
    /// </summary>
    [Fact]
    public async Task LeavesARepositoryWhoseStorageIsConfiguredElsewhereAlone()
    {
        var game = CreateRepository(_root, "game", (Oid("old"), 3_000));
        var gitDirectory = Path.Combine(game, ".git");
        Configured(gitDirectory, ("lfs.storage", Forward(Path.Combine(_temp.Path, "shared"))));
        var runner = Runner();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(DryRunsOf(runner));
        Assert.Contains(plan.Notes, n => n.Message.Contains("(lfs.storage)", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(gitDirectory, "lfs"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>lfs.storage</c> naming the store's own folder, relative to the git directory as git-lfs reads it, changes nothing.</summary>
    [Fact]
    public async Task OffersARepositoryWhoseStorageIsConfiguredAsItsOwnFolder()
    {
        var old = Oid("old");
        var game = CreateRepository(_root, "game", (old, 3_000));
        var gitDirectory = Path.Combine(game, ".git");
        Configured(gitDirectory, ("lfs.storage", "lfs"));
        DryRunReports(gitDirectory, Report(1, old));

        Assert.Equal(3_000, TheStep(await CreateProvider(Runner()).PlanAsync()).EstimatedBytes);
    }

    /// <summary>A store reached through a junction is how repositories share one, so it is not pruned.</summary>
    [Fact]
    public async Task LeavesAStoreReachedThroughALinkAlone()
    {
        var game = CreateRepository(_root, "game");
        var gitDirectory = Path.Combine(game, ".git");
        var shared = Path.Combine(_temp.Path, "shared-lfs");
        Directory.Delete(Path.Combine(gitDirectory, "lfs"), recursive: true);
        Directory.CreateDirectory(Path.Combine(shared, "objects"));
        Junction.ToDirectory(Path.Combine(gitDirectory, "lfs"), shared);
        var runner = Runner();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(runner.Invocations);
        Assert.Contains(plan.Notes, n => n.Message.Contains("reached through a link", StringComparison.Ordinal));
    }

    /// <summary>
    /// A submodule keeps its own store in the repository's git directory, and prune in the repository
    /// never reaches it, so it is its own item, pruned in its own checkout. Its name has a slash in it.
    /// </summary>
    [Fact]
    public async Task OffersASubmodulesStoreAsItsOwnItemInItsOwnCheckout()
    {
        var (top, art) = (Oid("top"), Oid("art"));
        var game = CreateRepository(_root, "game", (top, 3_000));
        var gitDirectory = Path.Combine(game, ".git");
        var module = CreateGitDirectory(Path.Combine(gitDirectory, "modules", "libs", "art"), (art, 4_000));
        var checkout = Path.Combine(game, "libs", "art");
        Directory.CreateDirectory(checkout);
        Configured(module, ("core.worktree", "../../../../libs/art"));
        DryRunReports(gitDirectory, Report(1, top));
        DryRunReports(module, Report(1, art));

        var plan = await CreateProvider(Runner()).PlanAsync();

        var steps = plan.Steps.OfType<RunCommandStep>().ToList();
        Assert.Equal(2, steps.Count);
        var submodule = Assert.Single(steps, s => s.Arguments.Contains(Forward(module), StringComparison.Ordinal));
        Assert.Contains($"\"--work-tree={Forward(checkout)}\"", submodule.Arguments, StringComparison.Ordinal);
        Assert.Equal(4_000, submodule.EstimatedBytes);
        Assert.Equal([new ItemFacet("Repository", $"art (submodule {Path.Combine("libs", "art")})")], submodule.Facets);
    }

    /// <summary>Without <c>core.worktree</c> nothing says where a submodule is checked out, so it is not pruned.</summary>
    [Fact]
    public async Task LeavesASubmoduleWithNoWorkTreeAlone()
    {
        var game = CreateRepository(_root, "game");
        Directory.Delete(Path.Combine(game, ".git", "lfs"), recursive: true);
        var module = CreateGitDirectory(Path.Combine(game, ".git", "modules", "art"), (Oid("art"), 4_000));
        var runner = Runner();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(DryRunsOf(runner));
        Assert.Contains(plan.Notes, n => n.Message.Contains("core.worktree is not set", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(module, "lfs"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Without Git LFS nothing can be pruned, and the row says so rather than reading as clear.</summary>
    [Fact]
    public async Task OffersNothingAndSaysSoWhenGitLfsIsNotInstalled()
    {
        CreateRepository(_root, "game", (Oid("old"), 3_000));
        var runner = Runner(lfsInstalled: false);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Empty(DryRunsOf(runner));
        Assert.Contains(plan.Notes, n => n.Message.Contains("Git LFS did not answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OffersNothingForARepositoryWhoseDryRunFailed()
    {
        var game = CreateRepository(_root, "game", (Oid("old"), 3_000));
        DryRunReports(Path.Combine(game, ".git"), "Prune error: Git can't resolve ref: \"HEAD\"", exitCode: 2);

        var plan = await CreateProvider(Runner()).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Git can't resolve ref", StringComparison.Ordinal));
    }

    /// <summary>A report that does not list as many objects as it counts is not measured some other way.</summary>
    [Fact]
    public async Task OffersNothingWhereTheDryRunDoesNotListEveryObject()
    {
        var (old, older) = (Oid("old"), Oid("older"));
        var game = CreateRepository(_root, "game", (old, 3_000), (older, 3_000));
        DryRunReports(Path.Combine(game, ".git"), $"2 local objects, 0 retained, done.\n\n * {older} (3 KB), done.\n");

        var plan = await CreateProvider(Runner()).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("did not list every old file", StringComparison.Ordinal));
    }

    /// <summary>A repository something is working in is not pruned, and git-lfs is not asked about it.</summary>
    [Fact]
    public async Task LeavesARepositoryInUseAlone()
    {
        var game = CreateRepository(_root, "game", (Oid("old"), 3_000));
        var gitDirectory = Path.Combine(game, ".git");
        var runner = Runner();

        var plan = await CreateProvider(runner, new FakeLiveTreeInspector(gitDirectory)).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(DryRunsOf(runner));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(gitDirectory, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Asked again immediately before prune runs: a program started in the repository since the
    /// preview, or a link made to the store, holds the step back.
    /// </summary>
    [Fact]
    public async Task TheCleanIsHeldBackByAProgramStartedInTheRepositoryOrALinkedStore()
    {
        var old = Oid("old");
        var game = CreateRepository(_root, "game", (old, 3_000), (Oid("current"), 1_000));
        var gitDirectory = Path.Combine(game, ".git");
        DryRunReports(gitDirectory, Report(2, old));
        var liveTrees = new FakeLiveTreeInspector();

        var step = TheStep(await CreateProvider(Runner(), liveTrees).PlanAsync());
        var check = Assert.IsType<GitLfsPruneTarget>(step.TargetCheck);

        Assert.Null(check.WhyNot(step, CancellationToken.None));
        Assert.Contains(IUseCheck.ReadingLimit, liveTrees.AgeLimits);
        Assert.Equal([(gitDirectory, game)], liveTrees.Asked.Select(q => (q.Directory, q.Project)));

        Assert.Contains(
            $"is working in {game}",
            new GitLfsPruneTarget(new FakeLiveTreeInspector(gitDirectory), CheckoutOf(game)).WhyNot(step, CancellationToken.None),
            StringComparison.Ordinal);
        Assert.Contains(
            LiveTreeVeto.CannotTell,
            new GitLfsPruneTarget(FakeLiveTreeInspector.CannotTell, CheckoutOf(game)).WhyNot(step, CancellationToken.None),
            StringComparison.Ordinal);

        var store = Path.Combine(gitDirectory, "lfs");
        var moved = Path.Combine(_temp.Path, "moved-lfs");
        Directory.Move(store, moved);
        Junction.ToDirectory(store, moved);

        Assert.Contains("reached through a link", check.WhyNot(step, CancellationToken.None), StringComparison.Ordinal);
    }

    private static GitLfsCheckout CheckoutOf(string workTree) =>
        new(new GitLfsStore(Path.Combine(workTree, ".git"), Submodule: null), workTree);

    [Fact]
    public async Task IsNotPresentWithoutGit()
    {
        var provider = new GitLfsObjectProvider(
            _roots,
            new SourceDirectoryDiscovery(new FakeDirectoryScanner(), new FakeVolumeInventory()),
            FakeLiveTreeInspector.NothingLive,
            new FakeUserEnvironment(_temp.Path),
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            new FakeDirectoryScanner());

        Assert.False(await provider.IsPresentAsync());
        Assert.True(await CreateProvider(Runner()).IsPresentAsync());
    }
}
