using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Poetry is the §5.1 provider whose cache directory also holds the thing that must never go: its
/// virtual environments default to a child of the very folder being cleaned. So most of what is
/// asserted here is the negative — what the plan leaves alone, and what it proves survived (§5.6) —
/// rather than what it removes.
///
/// <para>Everything runs against a synthetic profile and canned Poetry output. Poetry is not
/// installed on the machine this was written on, which is exactly the case the
/// <see cref="IProcessRunner"/> seam exists for.</para>
/// </summary>
public sealed class PoetryCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public PoetryCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private PoetryCacheProvider CreateProvider(FakeProcessRunner? runner = null) =>
        new(_environment, runner ?? new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>Poetry's default cache directory, without creating anything in it.</summary>
    private string CacheRoot => Path.Combine(_environment.LocalAppData, "pypoetry", "Cache");

    /// <summary>
    /// The layout Poetry actually builds: downloaded artefacts, one metadata cache per repository,
    /// and the environments beside both of them.
    /// </summary>
    private (string Artifacts, string Repositories, string Environments) CreateCache(string? root = null)
    {
        root ??= CacheRoot;

        var artifacts = Path.Combine(root, "artifacts");
        var repositories = Path.Combine(root, "cache", "repositories");
        var environments = Path.Combine(root, "virtualenvs");

        Directory.CreateDirectory(Path.Combine(artifacts, "ab", "cd"));
        File.WriteAllBytes(Path.Combine(artifacts, "ab", "cd", "package.whl"), new byte[4096]);

        Directory.CreateDirectory(Path.Combine(repositories, "PyPI"));
        File.WriteAllBytes(Path.Combine(repositories, "PyPI", "metadata.json"), new byte[2048]);

        Directory.CreateDirectory(Path.Combine(environments, "myproject-py3.12", "Lib"));
        File.WriteAllBytes(Path.Combine(environments, "myproject-py3.12", "Lib", "installed.pyd"), new byte[8192]);

        return (artifacts, repositories, environments);
    }

    /// <summary>Poetry installed, answering its two config lookups and naming one cache.</summary>
    private FakeProcessRunner Poetry(string? cacheRoot = null, string? environments = null)
    {
        _environment.WithExecutable("poetry");

        return new FakeProcessRunner()
            .Responding("config cache-dir", cacheRoot ?? CacheRoot)
            .Responding("config virtualenvs.path", environments ?? Path.Combine(cacheRoot ?? CacheRoot, "virtualenvs"))
            .Responding("cache list", "PyPI");
    }

    [Fact]
    public async Task ReportsNotPresentWhenPoetryWasNeverInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    [Fact]
    public async Task AsksPoetryWhereItsCacheIsRatherThanAssuming()
    {
        var elsewhere = _temp.CreateDirectory("relocated-cache");
        CreateCache(elsewhere);

        var runner = Poetry(elsewhere);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(runner.Invocations, i => i.Arguments.Contains("config cache-dir", StringComparison.Ordinal));
        Assert.Contains(Path.Combine(elsewhere, "artifacts"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.Combine(CacheRoot, "artifacts"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The environments are a setting of their own, so a machine that moved them and left the cache
    /// where it was must be described as it is. Assuming the default would name a directory Poetry
    /// has stopped using, and then prove nothing about the one it uses now.
    /// </summary>
    [Fact]
    public async Task AsksPoetryWhereItsVirtualEnvironmentsAreRatherThanAssuming()
    {
        CreateCache();
        var moved = _temp.CreateDirectory("relocated-environments");

        var runner = Poetry(environments: moved);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(
            runner.Invocations, i => i.Arguments.Contains("config virtualenvs.path", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    [Fact]
    public async Task AsksPoetryNotToColouriseTheOutputItIsAboutToParse()
    {
        CreateCache();

        var runner = Poetry();
        await CreateProvider(runner).PlanAsync();

        // Without this, Poetry wraps its output in ANSI escapes and they land inside the parsed
        // path and inside the cache names handed back to it.
        Assert.All(
            runner.Invocations,
            i => Assert.Contains("--no-ansi", i.Arguments, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FallsBackToTheDocumentedLocationWhenPoetryCannotAnswer()
    {
        _environment.WithExecutable("poetry");
        CreateCache();

        var runner = new FakeProcessRunner()
            .Responding("config", string.Empty, exitCode: 1)
            .Responding("cache list", "PyPI");

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(Path.Combine(CacheRoot, "artifacts"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(CacheRoot, "virtualenvs"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The issue this provider exists for. Every environment on the machine sits inside the folder
    /// being cleaned, and each is a full install rather than a cache.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheVirtualEnvironmentsThatLiveInsideTheCache()
    {
        var (_, _, environments) = CreateCache();

        var plan = await CreateProvider(Poetry()).PlanAsync();

        Assert.DoesNotContain(environments, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        // Contains(ancestor, candidate). The question is whether a measured path lies inside the
        // environments, so the environments are the ancestor — the other order asks whether a
        // measured path *contains* them, which no realistic over-reach would trip.
        Assert.All(
            plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths),
            path => Assert.False(LongPath.Contains(environments, path)));

        // Not merely unmentioned — asserted to survive (§5.6).
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(environments, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    [Fact]
    public async Task NeverTargetsTheCacheRootBecauseTheEnvironmentsAreInsideIt()
    {
        CreateCache();

        var plan = await CreateProvider(Poetry()).PlanAsync();

        Assert.DoesNotContain(CacheRoot, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(CacheRoot, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    /// <summary>
    /// §5.2's unrecognised case, which is the direction that loses data: a child nobody wrote a rule
    /// about is Tier 4, and the user is told rather than left to notice the omission.
    /// </summary>
    [Fact]
    public async Task LeavesAnUnrecognisedChildAloneAndSaysSo()
    {
        CreateCache();
        var unknown = Path.Combine(CacheRoot, "something-poetry-added-later");
        Directory.CreateDirectory(unknown);
        File.WriteAllBytes(Path.Combine(unknown, "payload.bin"), new byte[4096]);

        var plan = await CreateProvider(Poetry()).PlanAsync();

        Assert.DoesNotContain(unknown, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("something-poetry-added-later", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>virtualenvs</c> is declared Tier 4 rather than left to fall through as unrecognised,
    /// and the declaration buys exactly one thing: the sentence the user reads. Nothing about
    /// what survives would change without it, because an unrecognised child is left alone
    /// anyway — so the wording is the whole of what there is to assert.
    /// </summary>
    [Fact]
    public async Task NamesWhatTheVirtualEnvironmentsFolderHoldsRatherThanCallingItUnrecognised()
    {
        CreateCache();

        var plan = await CreateProvider(Poetry()).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("Leaving 'virtualenvs' alone", StringComparison.Ordinal)
            && n.Message.Contains("full dependency install", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.1 answers the repository caches and nothing else. Poetry's clear builds a file cache over
    /// its repository directory and flushes that; the archives it downloaded and the wheels it built
    /// sit in a sibling no Poetry command removes, so the plan removes that one by path.
    /// </summary>
    [Fact]
    public async Task DeletesTheArtifactsCacheBecauseNoPoetryCommandReachesIt()
    {
        var (artifacts, _, _) = CreateCache();

        var plan = await CreateProvider(Poetry()).PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<DeleteDirectoryStep>());
        Assert.Equal(artifacts, step.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(step.EstimatedBytes > 0);
    }

    [Fact]
    public async Task ClearsEachNamedRepositoryCacheWithPoetrysOwnCommand()
    {
        var (_, repositories, _) = CreateCache();
        Directory.CreateDirectory(Path.Combine(repositories, "private-index"));
        File.WriteAllBytes(Path.Combine(repositories, "private-index", "metadata.json"), new byte[1024]);

        var runner = Poetry().Responding("cache list", "PyPI\nprivate-index");
        var plan = await CreateProvider(runner).PlanAsync();

        var commands = plan.Steps.OfType<RunCommandStep>().ToList();

        Assert.Equal(2, commands.Count);
        Assert.Contains(commands, s => s.Arguments.Contains("cache clear PyPI --all", StringComparison.Ordinal));
        Assert.Contains(commands, s => s.Arguments.Contains("cache clear private-index --all", StringComparison.Ordinal));

        // The repository cache is measured but never targeted: §5.1 leaves Poetry deciding what it
        // removes, so the paths on a command step are a probe rather than a target.
        Assert.All(
            commands.SelectMany(s => s.MeasuredPaths),
            path => Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>poetry cache clear</c> asks "Delete N entries?" before it does anything, and Deguffer
    /// starts it with no console attached. Without the flag the step's behaviour would depend on
    /// what a detached standard input does to a prompt.
    /// </summary>
    [Fact]
    public async Task AsksPoetryNotToPromptBecauseThereIsNoConsoleToAnswerThePrompt()
    {
        CreateCache();

        var plan = await CreateProvider(Poetry()).PlanAsync();

        Assert.All(
            plan.Steps.OfType<RunCommandStep>(),
            step => Assert.Contains("--no-interaction", step.Arguments, StringComparison.Ordinal));
    }

    /// <summary>
    /// The directory holding the repository caches is recognised as disposable and is still not a
    /// target. Poetry's own command clears what is inside it, and §5.1 leaves that route in charge.
    /// </summary>
    [Fact]
    public async Task NeverDeletesTheDirectoryHoldingTheRepositoryCachesByPath()
    {
        CreateCache();

        var plan = await CreateProvider(Poetry()).PlanAsync();

        Assert.DoesNotContain(
            Path.Combine(CacheRoot, "cache"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(CacheRoot, "cache"), StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    /// <summary>
    /// A cache name reaches Deguffer as a directory name written by somebody else and leaves it as
    /// the arguments of a process this tool starts. A name carrying a space or a shell character
    /// would change which command runs, so it is left to Poetry rather than quoted at.
    /// </summary>
    [Fact]
    public async Task RefusesACacheNameThatWouldChangeWhichCommandRuns()
    {
        var (_, repositories, _) = CreateCache();
        Directory.CreateDirectory(Path.Combine(repositories, "odd name"));

        var runner = Poetry().Responding("cache list", "PyPI\nodd name");
        var plan = await CreateProvider(runner).PlanAsync();

        var command = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains("cache clear PyPI --all", command.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("odd name", command.Arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.2. A cache name is a directory name Poetry read off the disk, and <c>.</c> and <c>..</c>
    /// are ordinary command-line tokens that walk out of the directory the step claims to be about.
    /// <c>..</c> resolves to the container this provider's own §5.6 list says must survive, and
    /// Poetry's clear flushes whatever it is pointed at — so it would take every environment
    /// underneath. Poetry's own containment check compares the paths without resolving them, so it
    /// does not catch this either.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task RefusesACacheNameThatWalksOutOfTheRepositoryCache(string name)
    {
        var (_, repositories, _) = CreateCache();

        var runner = Poetry().Responding("cache list", $"PyPI\n{name}");
        var plan = await CreateProvider(runner).PlanAsync();

        var command = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains("cache clear PyPI --all", command.Arguments, StringComparison.Ordinal);

        // Whatever it measures is inside the repository cache, and is not the repository cache.
        var measured = Assert.Single(command.MeasuredPaths);
        Assert.Equal(repositories, Path.GetDirectoryName(measured), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Both settings are configurable and neither constrains the other, so a <c>virtualenvs.path</c>
    /// at or above the cache directory makes every child of it part of the environment tree. The
    /// name-based rule cannot see that, which is why the resolved paths are compared.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhenTheEnvironmentsAreConfiguredToHoldTheCache()
    {
        CreateCache();

        var runner = Poetry(environments: CacheRoot);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("leaving the whole of", StringComparison.Ordinal));
    }

    /// <summary>The same refusal one level in, where a recognised child holds the environments.</summary>
    [Fact]
    public async Task LeavesARecognisedChildAloneWhenTheEnvironmentsWereMovedInsideIt()
    {
        var (artifacts, _, _) = CreateCache();
        var inside = Path.Combine(artifacts, "envs");
        Directory.CreateDirectory(inside);

        var runner = Poetry(environments: inside);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.DoesNotContain(artifacts, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("virtual environments inside it", StringComparison.Ordinal));
    }

    /// <summary>
    /// The one value that falls between the other two containment checks.
    /// <c>{cache-dir}\cache</c> holds the repository cache rather than sitting inside it, and
    /// it is not the cache root either, so neither the whole-root refusal nor the per-child one
    /// sees it. Poetry's own clear would otherwise run against a directory inside the
    /// environment tree.
    /// </summary>
    [Fact]
    public async Task DoesNotRunPoetrysClearWhenTheEnvironmentsHoldTheRepositoryCache()
    {
        CreateCache();

        var runner = Poetry(environments: Path.Combine(CacheRoot, "cache"));
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("same tree as its own repository cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoesNotLookThroughACacheDirectoryThatIsALink()
    {
        var real = _temp.CreateDirectory("elsewhere");
        CreateCache(real);

        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "pypoetry"));
        Directory.CreateSymbolicLink(CacheRoot, real);

        var plan = await CreateProvider(Poetry()).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeavesALinkedChildAloneAndSaysSo()
    {
        CreateCache();
        Directory.Delete(Path.Combine(CacheRoot, "artifacts"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(CacheRoot, "artifacts"), _temp.CreateDirectory("far-side"));

        var plan = await CreateProvider(Poetry()).PlanAsync();

        Assert.Empty(plan.Steps.OfType<DeleteDirectoryStep>());
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("delete through a link", StringComparison.Ordinal));
    }

    /// <summary>
    /// The §5.6 negative after a real execution rather than a simulated one: the plan is carried out
    /// against a real tree by the real executor and the real directory remover, and what the run has
    /// to show afterwards is the environments still standing.
    ///
    /// <para>Only the subprocess is faked, which is the point. Poetry is not installed on the
    /// machine this was written on, and the deletion half of the plan is Deguffer's own — so the
    /// half that can destroy something runs for real, and the half that belongs to Poetry does not
    /// run at all.</para>
    /// </summary>
    [Fact]
    public async Task ExecutingRemovesTheArtifactsAndLeavesTheEnvironmentsStanding()
    {
        var (artifacts, repositories, environments) = CreateCache();
        var installed = Path.Combine(environments, "myproject-py3.12", "Lib", "installed.pyd");

        var runner = Poetry();
        var provider = CreateProvider(runner);

        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(artifacts));

        // §5.6, asserted rather than inferred from the absence of a step naming them.
        Assert.True(Directory.Exists(environments));
        Assert.True(File.Exists(installed));
        Assert.True(Directory.Exists(CacheRoot));
        Assert.True(Directory.Exists(repositories));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);

        // §5.1's half was handed to Poetry rather than done by path.
        Assert.Contains(runner.Invocations, i =>
            i.Arguments.Contains("cache clear PyPI --all", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(repositories, "PyPI")));
    }

    /// <summary>
    /// §5.6, and the reason the default location is named as well as the configured one.
    /// Moving <c>virtualenvs.path</c> does not move the environments already on disk: Poetry
    /// starts creating new ones at the new location and leaves the old tree exactly where it
    /// was. A §5.6 list built only from the current setting would produce no evidence at all
    /// about the environments a machine actually has.
    /// </summary>
    [Fact]
    public async Task ProvesTheDefaultEnvironmentsSurvivedAfterVirtualenvsPathWasMoved()
    {
        var (_, _, environments) = CreateCache();
        var moved = _temp.CreateDirectory("relocated-environments");

        var plan = await CreateProvider(Poetry(environments: moved)).PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(environments, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    /// <summary>§5.6: the negative is the whole test. An over-broad rule passes every positive one.</summary>
    [Fact]
    public async Task VerificationFailsLoudlyIfTheVirtualEnvironmentsVanished()
    {
        var (_, _, environments) = CreateCache();

        var provider = CreateProvider(Poetry());
        var plan = await provider.PlanAsync();

        Assert.True((await provider.VerifyAsync(plan)).Passed);

        // Simulate the over-broad rule §5.6 exists to catch: clearing the cache took the
        // environments with it.
        Directory.Delete(environments, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c =>
            c.Path.Equals(environments, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReResolvesBothSettingsAfterInvalidationBecauseEitherCanMove()
    {
        var first = _temp.CreateDirectory("first-cache");
        CreateCache(first);

        var moved = _temp.CreateDirectory("moved-cache");
        CreateCache(moved);

        var runner = Poetry(first);
        var provider = CreateProvider(runner);

        var before = await provider.PlanAsync();
        Assert.Contains(Path.Combine(first, "artifacts"), before.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        // POETRY_CACHE_DIR moved between scans; the planner invalidates before replanning.
        runner.Responding("config cache-dir", moved)
            .Responding("config virtualenvs.path", Path.Combine(moved, "virtualenvs"));
        provider.InvalidateCaches();

        var after = await provider.PlanAsync();

        Assert.Contains(Path.Combine(moved, "artifacts"), after.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(first, "artifacts"), after.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// This is the only provider that measures twice — once for the command route and once for the
    /// path route — so it is the only one that can report the same scan twice. The plan reached the
    /// user with §5.5's "scanned by walking directories" paragraph printed under itself, reading as
    /// two separate findings about one directory.
    /// </summary>
    [Fact]
    public async Task SaysEachThingOnceEvenThoughItMeasuresTwice()
    {
        CreateCache();

        // The duplicate was §5.5's scan-route sentence, which the real scanner produces only where
        // the file-table path did not serve the measurement. Stamping the reason on the walk makes
        // the sentence certain rather than dependent on whether the test host happens to be
        // elevated — otherwise this test would pass vacuously on half the machines it runs on.
        var walked = ParallelEnumerationScanner.Default.Because(FallbackReason.NotElevated);

        var provider = new PoetryCacheProvider(
            _environment, Poetry(), FakeProcessInspector.NothingRunning, walked);

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Message.StartsWith("Scanned by walking directories", StringComparison.Ordinal));

        Assert.Equal(
            plan.Notes.Count,
            plan.Notes.Select(n => n.Message).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task WarnsWhenPoetryIsRunning()
    {
        CreateCache();

        var provider = new PoetryCacheProvider(
            _environment, Poetry(), new FakeProcessInspector("poetry"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task SaysSoWhenPoetryIsInstalledButItsCacheDirectoryIsNotThereYet()
    {
        _environment.WithExecutable("poetry");

        var plan = await CreateProvider(Poetry()).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("does not exist yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaysSoWhenTheCacheDirectoryIsThereButEmpty()
    {
        Directory.CreateDirectory(CacheRoot);

        var plan = await CreateProvider(Poetry()).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("has cached nothing yet", StringComparison.Ordinal));
    }
}
