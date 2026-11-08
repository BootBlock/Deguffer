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
/// negative assertions carry the weight: the repository, its git directory and its store are protected
/// and must outlive the prune, nothing in a git directory is ever a path target (§5.2), and a store
/// Git LFS could not check against a server is never offered, because there its prune can remove the
/// only copy of a file. Which objects go is prune's to decide (§5.1), so the figure counts only those
/// its dry run names.
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

    /// <summary>The remote every repository has unless a test says otherwise.</summary>
    private static readonly (string Key, string Value) Origin = ("remote.origin.url", "https://example.com/studio/game.git");

    /// <summary>What <c>git lfs env</c> prints of a repository whose one remote is a server, as git-lfs 3.8.1 prints it.</summary>
    private const string ServerEnvironment =
        "git-lfs/3.8.1 (GitHub; windows amd64; go 1.27.3; git a662a6db)\ngit version 2.55.0.windows.5\n\n"
        + "Endpoint=https://example.com/studio/game.git/info/lfs (auth=none)\n"
        + "LocalMediaDir=C:\\Users\\testuser\\src\\game\\.git\\lfs\\objects\n"
        + "PruneRemoteName=origin\n";

    /// <summary>What each git directory's <c>git lfs env</c> answers, by the forward-slash form git is given.</summary>
    private readonly Dictionary<string, CommandOutcome> _environments = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// git's answer to the configuration query, as <c>config -z --get-regexp</c> prints it and as the
    /// runner hands it over: line by line, each line ended with <see cref="Environment.NewLine"/>, so
    /// the line feed after each key arrives as a carriage return and a line feed, and one more ends the
    /// output.
    /// </summary>
    private void Configured(string gitDirectory, params (string Key, string Value)[] entries) =>
        _configuration[Forward(gitDirectory)] =
            string.Concat(entries.Select(entry => $"{entry.Key}\n{entry.Value}\0")).ReplaceLineEndings() + Environment.NewLine;

    private void Described(string gitDirectory, string environment, int exitCode = 0) =>
        _environments[Forward(gitDirectory)] = new CommandOutcome(exitCode, environment.ReplaceLineEndings(), string.Empty);

    /// <summary>
    /// git as the plan meets it: Git LFS installed, every configuration query answered from
    /// <see cref="_configuration"/> or with the <see cref="Origin"/> remote alone, every
    /// <c>git lfs env</c> from <see cref="_environments"/> or with <see cref="ServerEnvironment"/>, and
    /// every dry run from <see cref="_dryRuns"/>. <paramref name="answering"/> sees each run before git
    /// answers it, so a test can decide the order git answers in.
    /// </summary>
    private FakeProcessRunner Runner(bool lfsInstalled = true, Action<string>? answering = null) =>
        new FakeProcessRunner().Replying(Git, arguments =>
        {
            answering?.Invoke(arguments);

            if (arguments == "lfs version")
            {
                return lfsInstalled
                    ? new CommandOutcome(0, "git-lfs/3.8.1 (GitHub; windows amd64; go 1.27.3; git a662a6db)\n", string.Empty)
                    : new CommandOutcome(1, string.Empty, "git: 'lfs' is not a git command. See 'git --help'.");
            }

            var gitDirectory = GitDirectoryIn(arguments);

            if (arguments.Contains(" config -z --get-regexp ", StringComparison.Ordinal))
            {
                return new CommandOutcome(
                    0,
                    _configuration.TryGetValue(gitDirectory, out var set) ? set : $"{Origin.Key}\r\n{Origin.Value}\0\r\n",
                    string.Empty);
            }

            if (arguments.EndsWith(" lfs env", StringComparison.Ordinal))
            {
                return _environments.TryGetValue(gitDirectory, out var described)
                    ? described
                    : new CommandOutcome(0, ServerEnvironment.ReplaceLineEndings(), string.Empty);
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

    /// <summary>
    /// git writes paths and configuration values in UTF-8 whatever the console's code page, so every
    /// git query and the prune itself are read that way, rather than mangled in a console's OEM code
    /// page.
    /// </summary>
    [Fact]
    public async Task ReadsEveryGitOutputAsUtf8()
    {
        var old = Oid("old");
        var game = CreateRepository(_root, "game", (old, 3_000));
        DryRunReports(Path.Combine(game, ".git"), Report(1, old));
        var runner = Runner();

        var step = TheStep(await CreateProvider(runner).PlanAsync());

        Assert.Equal(ToolOutputEncoding.Utf8, step.Output);
        Assert.Equal(4, runner.Invocations.Count);
        Assert.All(runner.Invocations, invocation => Assert.Equal(ToolOutputEncoding.Utf8, invocation.Run.Output));
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
            + "-c \"lfs.pruneremotetocheck=origin\" -c credential.interactive=never lfs prune --verify-remote",
            step.Arguments);
        Assert.Equal(CancelledCommand.Ended, step.WhenCancelled);
    }

    /// <summary>
    /// The dry run is pinned exactly as the prune is, forces the progress that lists every object, only
    /// reads, and is read as the UTF-8 git writes.
    /// </summary>
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
            + "-c \"lfs.pruneremotetocheck=origin\" -c lfs.forceprogress=true lfs prune --dry-run --verbose",
            dryRun.Arguments);
        Assert.Equal(ToolRun.Query.WritingIn(ToolOutputEncoding.Utf8), dryRun.Run);
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
        Configured(gitDirectory, ("lfs.storage", "lfs"), Origin);
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
        Configured(module, ("core.worktree", "../../../../libs/art"), Origin);
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
        Assert.Equal([(gitDirectory, game)], liveTrees.Asked.Select(q => (q.Directory, q.Project)));

        // A program started in the work tree after the preview, asked about once the reading is past the limit.
        liveTrees.WithProgram("git", workingDirectory: game).PassTheReadingLimit();

        Assert.Contains($"is working in {game}", check.WhyNot(step, CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains(IUseCheck.ReadingLimit, liveTrees.AgeLimits);

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
        new(new GitLfsStore(Path.Combine(workTree, ".git"), Submodule: null), workTree, ["origin"]);

    /// <summary>
    /// Git LFS checks nothing before it removes a file when the remote is a folder, so an object only
    /// the reflog names, never pushed, would go with no copy anywhere. Such a repository is not offered,
    /// and its dry run is never run.
    /// </summary>
    [Theory]
    [InlineData("file:///D:/Backups/game.git")]
    [InlineData("file:////nas/repos/game.git")]
    [InlineData("")]
    public async Task LeavesARepositoryWhoseRemoteIsNotAServerAlone(string endpoint)
    {
        var game = CreateRepository(_root, "game", (Oid("old"), 3_000));
        var gitDirectory = Path.Combine(game, ".git");
        Described(gitDirectory, $"Endpoint={endpoint} (auth=none)\nPruneRemoteName=origin\n");
        var runner = Runner();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
        Assert.Empty(DryRunsOf(runner));
        Assert.Contains(plan.Notes, n => n.Message.Contains("which is not a server", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(gitDirectory, "lfs"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>With no remote of the name prune checks, nothing counts as pushed and nothing is there to ask.</summary>
    [Fact]
    public async Task LeavesARepositoryWithoutTheRemotePruneChecksAlone()
    {
        var game = CreateRepository(_root, "game", (Oid("old"), 3_000));
        Configured(Path.Combine(game, ".git"));
        var runner = Runner();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(DryRunsOf(runner));
        Assert.Contains(plan.Notes, n => n.Message.Contains("has no remote called 'origin'", StringComparison.Ordinal));
    }

    /// <summary>
    /// The endpoint judged is the one prune checks, named or the default, never another remote's; and
    /// the remote is pinned on both commands, so the clean checks the one the plan judged.
    /// </summary>
    [Theory]
    [InlineData("backup", "https://example.com/studio/game.git/info/lfs", "file:///D:/Backups/game.git", false)]
    [InlineData("backup", "file:///D:/Backups/game.git", "https://example.com/studio/game.git/info/lfs", true)]
    [InlineData("origin", "https://example.com/studio/game.git/info/lfs", "file:///D:/Backups/game.git", true)]
    public async Task JudgesTheEndpointOfTheRemotePruneChecks(string checkedRemote, string defaultEndpoint, string backupEndpoint, bool offered)
    {
        var old = Oid("old");
        var game = CreateRepository(_root, "game", (old, 3_000));
        var gitDirectory = Path.Combine(game, ".git");
        Configured(gitDirectory, Origin, ("remote.backup.url", "D:/Backups/game.git"));
        Described(
            gitDirectory,
            $"Endpoint={defaultEndpoint} (auth=none)\nEndpoint (backup)={backupEndpoint} (auth=none)\nPruneRemoteName={checkedRemote}\n");
        DryRunReports(gitDirectory, Report(1, old));
        var runner = Runner();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Equal(offered, !plan.IsEmpty);

        if (offered)
        {
            var pin = $"-c \"lfs.pruneremotetocheck={checkedRemote}\"";
            Assert.Contains(pin, TheStep(plan).Arguments, StringComparison.Ordinal);
            Assert.Contains(pin, Assert.Single(runner.Invocations, i => i.Arguments.Contains("--dry-run", StringComparison.Ordinal)).Arguments, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OffersNothingAndSaysSoWhereGitLfsCannotDescribeTheRepository()
    {
        var game = CreateRepository(_root, "game", (Oid("old"), 3_000));
        Described(Path.Combine(game, ".git"), "Error: not a git repository", exitCode: 2);

        var plan = await CreateProvider(Runner()).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Git LFS could not describe game", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7.1: a repository the plan holds back because a program works in it is declared, so Explore
    /// refuses its store and its git directory too; one nothing works in is not.
    /// </summary>
    [Fact]
    public async Task DeclaresARepositoryInUseSoExploreRefusesIt()
    {
        var busy = CreateRepository(_root, "busy", (Oid("old"), 3_000));
        var idle = CreateRepository(_root, "idle", (Oid("older"), 3_000));
        var liveTrees = new FakeLiveTreeInspector().WithProgram("Code", workingDirectory: busy);

        var roots = await CreateProvider(Runner(), liveTrees).DiscoverToolRootsAsync();

        var declared = Assert.Single(roots);
        Assert.Equal(Path.Combine(busy, ".git"), declared.Path, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(roots, root => root.Path.StartsWith(idle, StringComparison.OrdinalIgnoreCase));

        var system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        var policy = await Deguffer.Core.Exploring.Acting.ExploreActionPolicy.ForAsync(
            system, _environment, new FakeVolumeInventory(), [CreateProvider(Runner(), liveTrees)]);

        Assert.False(policy.MayRemove(Path.Combine(busy, ".git", "lfs", "objects")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(busy, ".git")).IsAllowed);
    }

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

    /// <summary>
    /// Each store is a repository of its own and no store's answer feeds another's question, so git is
    /// asked about several at once: every store's settings are read, and every dry run started, before
    /// git has answered any of them. Asked one store after another, the first held run waits for a
    /// second that cannot start until it ends. §5.6 for a plan built this way: every repository's
    /// checkout, git directory and store, a store reached through a link, and a folder named
    /// <c>.git</c> that is not a git directory (§5.2), all survive.
    /// </summary>
    [Fact]
    public async Task AsksGitAboutSeveralStoresAtOnceAndStillProtectsEachOne()
    {
        string[] names = ["alpha", "bravo", "charlie"];
        var repositories = names.Select(name => CreateRepository(_root, name, (Oid(name), 2_000))).ToList();
        repositories.ForEach(repository => DryRunReports(Path.Combine(repository, ".git"), Report(1, Oid(Path.GetFileName(repository)))));
        var impostor = Path.Combine(_root, "notes", ".git");
        Directory.CreateDirectory(impostor);
        var linked = Path.Combine(CreateRepository(_root, "delta"), ".git", "lfs");
        var shared = Path.Combine(_temp.Path, "shared-lfs");
        Directory.Delete(linked, recursive: true);
        Directory.CreateDirectory(Path.Combine(shared, "objects"));
        Junction.ToDirectory(linked, shared);
        var settings = new HeldRuns(together: names.Length);
        var dryRuns = new HeldRuns(together: names.Length);
        var runner = Runner().Holding(Git, " config -z --get-regexp ", settings).Holding(Git, " lfs prune --dry-run ", dryRuns);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(settings.StartedTogether, "The stores' settings were read one store after another.");
        Assert.True(dryRuns.StartedTogether, "The stores' dry runs were run one store after another.");
        Assert.Equal(names.Length, plan.Steps.OfType<RunCommandStep>().Count());
        Assert.Empty(plan.TargetedPaths);

        string[] survivors =
        [
            .. repositories.SelectMany(repository => new[]
            {
                repository,
                Path.Combine(repository, ".git"),
                Path.Combine(repository, ".git", "HEAD"),
                Path.Combine(repository, ".git", "lfs", "objects"),
            }),
            impostor,
            linked,
        ];

        foreach (var survivor in survivors)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }
    }

    /// <summary>
    /// G4: however many stores there are, git is asked about no more than the bound at once, in either
    /// phase, and about more than one.
    ///
    /// <para>The lower bound is proven by order. The upper bound is not: a run that starts after the
    /// held set is released replies at once, so a lost bound shows only where a further run starts
    /// before the set has replied. That needs a machine with more logical processors than the bound,
    /// because with no bound given <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
    /// runs one body per processor, and it rests on scheduling rather than on order.</para>
    /// </summary>
    [Fact]
    public async Task AsksGitAboutNoMoreStoresAtOnceThanItsBound()
    {
        var count = GitLfsObjectProvider.StoresAtOnce * 3;

        for (var index = 0; index < count; index++)
        {
            var name = $"repository{index:00}";
            DryRunReports(Path.Combine(CreateRepository(_root, name, (Oid(name), 1_000)), ".git"), Report(1, Oid(name)));
        }

        var settings = new HeldRuns(together: GitLfsObjectProvider.StoresAtOnce);
        var dryRuns = new HeldRuns(together: GitLfsObjectProvider.StoresAtOnce);
        var runner = Runner().Holding(Git, " config -z --get-regexp ", settings).Holding(Git, " lfs prune --dry-run ", dryRuns);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Equal(count, plan.Steps.OfType<RunCommandStep>().Count());
        Assert.True(settings.StartedTogether);
        Assert.True(dryRuns.StartedTogether);
        Assert.InRange(settings.MostAtOnce, 2, GitLfsObjectProvider.StoresAtOnce);
        Assert.InRange(dryRuns.MostAtOnce, 2, GitLfsObjectProvider.StoresAtOnce);
    }

    /// <summary>
    /// git answers for the stores last first, in both phases, and the plan still reads in the stores'
    /// order: the notes of those Git LFS could not describe, then of those whose dry run failed, and the
    /// survivors of each.
    /// </summary>
    [Fact]
    public async Task ThePlanReadsInTheStoresOrderWhateverOrderGitAnswersIn()
    {
        string[] undescribed = ["alpha", "bravo", "charlie"];
        string[] failing = ["delta", "echo", "foxtrot"];
        string[] names = [.. undescribed, .. failing];
        var gitDirectories = names.ToDictionary(name => name, name => Path.Combine(CreateRepository(_root, name, (Oid(name), 1_000)), ".git"));

        foreach (var name in undescribed)
        {
            Described(gitDirectories[name], $"Error: {name} is not a git repository", exitCode: 2);
        }

        foreach (var name in failing)
        {
            DryRunReports(gitDirectories[name], $"Prune error: {name} cannot resolve HEAD", exitCode: 2);
        }

        var environments = new LastFirst(" lfs env", [.. undescribed.Select(name => gitDirectories[name])]);
        var dryRuns = new LastFirst(" lfs prune --dry-run ", [.. failing.Select(name => gitDirectories[name])]);

        var plan = await CreateProvider(Runner(answering: arguments =>
        {
            environments.Answer(arguments);
            dryRuns.Answer(arguments);
        })).PlanAsync();

        Assert.True(plan.IsEmpty);

        string[] said = [.. undescribed.Select(name => $"{name} is not a git repository"), .. failing.Select(name => $"{name} cannot resolve HEAD")];
        var notes = plan.Notes.ToList();
        var noted = said.Select(text => notes.FindIndex(n => n.Message.Contains(text, StringComparison.Ordinal))).ToList();
        Assert.DoesNotContain(-1, noted);
        Assert.Equal(noted.Order(), noted);

        var protectedPaths = plan.ProtectedPaths.ToList();
        var stores = names.Select(name => protectedPaths.FindIndex(p =>
            p.Path.Equals(Path.Combine(gitDirectories[name], "lfs"), StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.DoesNotContain(-1, stores);
        Assert.Equal(stores.Order(), stores);
    }

    /// <summary>
    /// Holds git's reply to <paramref name="command"/> for each of <paramref name="gitDirectories"/>
    /// until the one after it has been answered, so git answers for them last first. Asked one after
    /// another, the first waits for one that cannot be asked until it ends, and the test fails.
    /// </summary>
    private sealed class LastFirst(string command, IReadOnlyList<string> gitDirectories)
    {
        private readonly ManualResetEventSlim[] _answered = [.. gitDirectories.Select(_ => new ManualResetEventSlim())];

        public void Answer(string arguments)
        {
            if (!arguments.Contains(command, StringComparison.Ordinal))
            {
                return;
            }

            var asked = GitDirectoryIn(arguments);
            var index = gitDirectories.Select(Forward).ToList().FindIndex(d => d.Equals(asked, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return;
            }

            if (index + 1 < gitDirectories.Count && !_answered[index + 1].Wait(HeldRuns.DefaultDeadline))
            {
                throw new TimeoutException($"git was not asked '{command}' of {gitDirectories[index + 1]} while {gitDirectories[index]} waited.");
            }

            _answered[index].Set();
        }
    }
}
