using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The question the check at the clean asks the live-tree veto, built once for each reading of the
/// machine rather than once for each step (<see cref="LiveTreeQuestionSource"/>).
///
/// <para>For <c>obj</c>, building it lists every folder a program is in and parses the solutions
/// there, so a clean of hundreds of <c>obj</c> folders built the same question hundreds of times from
/// one reading. Each build reads where programs are once, which is what
/// <see cref="FakeLiveTreeInspector.OccupiedAskedCount"/> counts. A reading past
/// <see cref="IUseCheck.ReadingLimit"/> still builds a new one, so a project added to an open solution
/// during the clean is still seen.</para>
/// </summary>
public sealed class LiveTreeQuestionPerReadingTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;
    private readonly FakeLiveTreeInspector _liveTrees = FakeLiveTreeInspector.NothingLive;

    public LiveTreeQuestionPerReadingTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The cost removed. Visual Studio is open on a solution that names one project, and five others
    /// beside it are cleaned within one reading. Every check reads the solution's folder from the same
    /// reading, so it is read once, and the project the solution names still keeps its <c>obj</c>.
    /// </summary>
    [Fact]
    public async Task FiveObjStepsWithinOneReadingReadWhereProgramsAreOnce()
    {
        var solution = Path.Combine(ApproveRoot(), "Solution");
        var open = ProjectFixture.CreateProject(Path.Combine(solution, "Open"), "Open");
        var idle = Enumerable.Range(1, 5)
            .Select(i => ProjectFixture.CreateProject(Path.Combine(solution, $"Idle{i}"), $"Idle{i}"))
            .ToList();
        ProjectFixture.CreateSolution(solution, "Solution", xml: false, Path.Combine(solution, "Open", "Open.csproj"));
        _liveTrees.WithProgram("devenv", workingDirectory: solution);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // The premise: the solution was read, so the project it names was held back.
        Assert.Equal(idle.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        var before = _liveTrees.OccupiedAskedCount;
        var result = await provider.ExecuteAsync(plan);

        Assert.Equal(1, _liveTrees.OccupiedAskedCount - before);
        Assert.All(idle, obj => Assert.False(Directory.Exists(obj), $"{obj} was kept, so its check did not clear it"));
        Assert.True(Directory.Exists(open), "an obj the plan held back was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The bound kept. The checks of the first two steps answer from one reading, so the solution is
    /// read once for both. The project the third step removes is then added to the open solution and
    /// the reading passes the limit, so the third step's check reads the solution again and holds its
    /// <c>obj</c> back. The clean that follows keeps it, and §5.6 proves it standing.
    ///
    /// <para>Each check is asked directly, in order, because the clean runs several steps at once and
    /// so fixes no order between two checks a test could place the change in.</para>
    /// </summary>
    [Fact]
    public async Task AReadingDroppedBetweenTwoChecksReadsTheSolutionAgainAndHoldsBackAProjectAddedSince()
    {
        var solution = Path.Combine(ApproveRoot(), "Solution");
        var open = ProjectFixture.CreateProject(Path.Combine(solution, "Open"), "Open");
        var first = ProjectFixture.CreateProject(Path.Combine(solution, "First"), "First");
        var second = ProjectFixture.CreateProject(Path.Combine(solution, "Second"), "Second");
        var added = ProjectFixture.CreateProject(Path.Combine(solution, "Added"), "Added");
        var openProject = Path.Combine(solution, "Open", "Open.csproj");
        ProjectFixture.CreateSolution(solution, "Solution", xml: false, openProject);
        _liveTrees.WithProgram("devenv", workingDirectory: solution);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        _liveTrees.PassTheReadingLimit();

        var before = _liveTrees.OccupiedAskedCount;

        Assert.Empty(Ask(plan, first));
        Assert.Empty(Ask(plan, second));
        Assert.Equal(1, _liveTrees.OccupiedAskedCount - before);

        ProjectFixture.CreateSolution(
            solution, "Solution", xml: false, openProject, Path.Combine(solution, "Added", "Added.csproj"));
        _liveTrees.PassTheReadingLimit();

        Assert.Contains(Ask(plan, added), held => held.Reason.Contains("devenv", StringComparison.Ordinal));
        Assert.Equal(2, _liveTrees.OccupiedAskedCount - before);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(added), "an obj whose project was added to the open solution after the reading was dropped was removed");
        Assert.False(Directory.Exists(first), "an obj the solution never named was kept");
        Assert.False(Directory.Exists(second), "an obj the solution never named was kept");
        Assert.True(Directory.Exists(open), "an obj the plan held back was removed");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: devenv is working in Solution.");
        AssertProvedStanding(result, added);
    }

    /// <summary>
    /// What the shared question is kept against. The real inspector names one reading for every
    /// question within the limit, without reading the process table again, and a new one once the
    /// reading is past the limit or invalidated.
    /// </summary>
    [Fact]
    public void TheInspectorNamesOneReadingUntilItIsDropped()
    {
        var clock = new ManualTimeProvider();
        var calls = new FakeProcessTableCalls().With(FakeProcessTableCalls.Own);
        var inspector = new LiveTreeInspector(calls, new FakeVolumeInventory(), clock);

        var first = inspector.Reading();
        clock.Advance(IUseCheck.ReadingLimit);
        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

        Assert.Equal(first, inspector.Reading());
        Assert.Equal(1, calls.ListCount);

        clock.Advance(TimeSpan.FromTicks(1));
        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);
        var second = inspector.Reading();

        Assert.NotEqual(first, second);
        Assert.Equal(2, calls.ListCount);

        inspector.Invalidate();

        Assert.NotEqual(second, inspector.Reading());
        Assert.Equal(3, calls.ListCount);
    }

    private static IReadOnlyList<InUseNow> Ask(CleanupPlan plan, string path)
    {
        var step = Assert.Single(plan.Steps.OfType<DeleteStep>(), s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        return step.UseCheck!.Ask(step, CancellationToken.None);
    }

    private static void AssertProvedStanding(CleanupResult result, string path)
    {
        var check = Assert.Single(result.Verification!.Checks, c => c.Subject.Equals(path, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(VerificationOutcome.Survived, check.Outcome);
        Assert.True(result.Verification.Passed, result.Verification.Summary);
    }

    private string ApproveRoot()
    {
        var root = _temp.CreateDirectory("src");
        _roots.Save([new SourceRoot(root)]);
        return root;
    }

    private DotNetObjProvider CreateProvider()
    {
        var scanner = new FakeDirectoryScanner();

        return new DotNetObjProvider(
            _roots,
            new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()),
            _liveTrees,
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner);
    }
}
