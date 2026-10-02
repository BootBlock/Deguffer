using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7's second opinion, exercised at the point where it stops being one invocation.
///
/// A monorepo contributing several hundred deep paths overruns the command line CreateProcess will
/// accept, and the launch failure that follows arrives at the call site looking exactly like a
/// clean "nothing here is tracked". Those are the cases here: that the command is split, that the
/// split loses nothing, and that a question git declined to answer is never read as a yes.
///
/// The candidate directories are synthetic paths that need not exist — only the repository root is
/// on disk, because that is the sole part of this the filesystem is consulted for. That is what
/// makes a nine-hundred-directory repository a fast test.
/// </summary>
public sealed class TrackedFileCheckTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly string _repository;

    public TrackedFileCheckTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("git");
        _repository = _temp.CreateDirectory("repo");
        Directory.CreateDirectory(Path.Combine(_repository, ".git"));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Candidates deep enough that a few hundred of them exceed the budget, which is the shape a
    /// real monorepo has — nesting, not breadth, is what makes the command line long.
    /// </summary>
    private IReadOnlyList<string> Candidates(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i =>
            Path.Combine(_repository, "services", $"Service{i:D4}".PadRight(60, 'x'), "src", "obj")),
    ];

    private TrackedFileCheck Create(FakeProcessRunner runner) => new(_environment, runner);

    /// <summary>Git, where the environment says it is: the fake answers no other program.</summary>
    private string Git => _environment.FindExecutable("git")!;

    /// <summary>Git answering every listing with no tracked file.</summary>
    private FakeProcessRunner GitListingNothing() => new FakeProcessRunner().Responding(Git, "ls-files", string.Empty);

    /// <summary>
    /// The command is split, and the answer is the union of every part. A tracked file reported by
    /// the last invocation counts for exactly as much as one reported by the first — accumulating
    /// only until the first answer arrives would leave most of the repository unexamined while
    /// reporting a result.
    /// </summary>
    [Fact]
    public async Task SplitsALargeRepositoryAndUnionsWhatEachInvocationReports()
    {
        var candidates = Candidates(900);
        var first = candidates[0];
        var last = candidates[^1];

        // Each invocation answers only for what it was actually asked about, so a result attributed
        // to a batch that was never sent cannot pass this by accident.
        var runner = new FakeProcessRunner().Replying(Git, arguments =>
        {
            var listed = new[] { first, last }
                .Where(c => arguments.Contains(Pathspec(c), StringComparison.OrdinalIgnoreCase))
                .Select(c => Pathspec(c) + "/committed.props\0");

            return new CommandOutcome(0, string.Concat(listed), string.Empty);
        });

        var findings = await Create(runner).FindTrackedAsync(candidates);

        Assert.True(runner.Invocations.Count > 1, $"Expected a split, got {runner.Invocations.Count} invocation(s).");
        Assert.Equal([first, last], findings.Tracked.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(findings.Unanswered);
    }

    /// <summary>
    /// Every candidate is asked about exactly once. A dropped one is a directory the check silently
    /// vouched for without examining; a duplicated one is the process cost grouping exists to avoid.
    /// </summary>
    [Fact]
    public async Task NeitherDropsNorRepeatsACandidateAcrossTheSplit()
    {
        var candidates = Candidates(900);
        var runner = GitListingNothing();

        await Create(runner).FindTrackedAsync(candidates);

        var asked = runner.Invocations
            .SelectMany(i => i.Arguments.Split('"', StringSplitOptions.RemoveEmptyEntries))
            .Where(part => part.Contains("/obj", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(candidates.Count, asked.Count);
        Assert.Equal(
            candidates.Select(Pathspec).Order(StringComparer.OrdinalIgnoreCase),
            asked.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// No invocation may exceed what CreateProcess will accept, or the split has not solved the
    /// problem it exists for.
    /// </summary>
    [Fact]
    public async Task KeepsEveryInvocationInsideTheCommandLineLimit()
    {
        var runner = GitListingNothing();

        await Create(runner).FindTrackedAsync(Candidates(2000));

        // The executable path counts towards the limit too, so it is measured with the arguments
        // rather than the budget being checked against the arguments alone.
        Assert.All(runner.Invocations, i =>
        {
            var commandLine = i.FileName.Length + 1 + i.Arguments.Length;
            Assert.True(commandLine < 32_767, $"Command line was {commandLine} characters.");
        });
    }

    /// <summary>
    /// The fail-closed rule at batch granularity. One batch erroring declines the candidates in it
    /// and only those: the rest were answered for, and discarding them too would make one broken
    /// repository silently shrink the plan everywhere else.
    /// </summary>
    [Fact]
    public async Task DeclinesTheCandidatesInABatchGitWouldNotAnswerAndNoOthers()
    {
        var candidates = Candidates(900);
        var refused = candidates[^1];

        var runner = new FakeProcessRunner().Replying(Git, arguments =>
            arguments.Contains(Pathspec(refused), StringComparison.OrdinalIgnoreCase)
                ? new CommandOutcome(128, string.Empty, "fatal: index file corrupt")
                : null);

        var findings = await Create(runner).FindTrackedAsync(candidates);

        Assert.Contains(refused, findings.Unanswered);
        Assert.Empty(findings.Tracked);

        // Only the batch that failed is declined — everything git did answer for is still cleared.
        Assert.True(
            findings.Unanswered.Count < candidates.Count,
            "A single failed batch declined the whole repository.");
        Assert.DoesNotContain(candidates[0], findings.Unanswered);
    }

    /// <summary>
    /// Git absent is not git failing. There is no second opinion to be had, the recognition rule
    /// governs alone — the same protection every other provider relies on — and nothing is declined
    /// on the strength of a question that was never asked. Every candidate git would have been asked
    /// about is reported as unasked, so the plan can say the check did not run.
    /// </summary>
    [Fact]
    public async Task AsksNothingAndDeclinesNothingWhenGitIsNotInstalled()
    {
        var runner = new FakeProcessRunner();
        var candidates = Candidates(900);

        var findings = await new TrackedFileCheck(new FakeUserEnvironment(_temp.Path), runner)
            .FindTrackedAsync(candidates);

        Assert.Empty(runner.Invocations);
        Assert.Empty(findings.Tracked);
        Assert.Empty(findings.Unanswered);
        Assert.Equal(candidates.Count, findings.Unasked.Count);
        Assert.True(findings.Unasked.SetEquals(candidates));
    }

    /// <summary>
    /// A candidate outside any repository has no tracked files to ask about, whether git is
    /// installed or not. Reporting it as unasked would tell the user a check was skipped that could
    /// never have applied.
    /// </summary>
    [Fact]
    public async Task DoesNotReportACandidateOutsideAnyRepositoryAsUnasked()
    {
        var outside = Path.Combine(_temp.CreateDirectory("loose"), "Example", "obj");
        var inside = Candidates(1)[0];

        var findings = await new TrackedFileCheck(new FakeUserEnvironment(_temp.Path), new FakeProcessRunner())
            .FindTrackedAsync([outside, inside]);

        Assert.Equal([inside], findings.Unasked);
    }

    /// <summary>
    /// A repository nested inside another, such as a submodule, is its own repository. The walk up
    /// for <c>.git</c> is remembered between candidates, and what it remembers must not let a
    /// candidate in the outer repository take the inner one's root, or the reverse, whichever is
    /// walked first.
    /// </summary>
    [Fact]
    public async Task AsksEachNestedRepositoryAboutItsOwnCandidates()
    {
        var inner = Path.Combine(_repository, "external", "Library");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, ".git"), "gitdir: ../../.git/modules/Library");

        var innerFirst = Path.Combine(inner, "src", "A", "obj");
        var outer = Path.Combine(_repository, "external", "B", "obj");
        var innerSecond = Path.Combine(inner, "src", "C", "obj");
        var runner = GitListingNothing();

        await Create(runner).FindTrackedAsync([innerFirst, outer, innerSecond]);

        Assert.Equal(2, runner.Invocations.Count);
        Assert.Contains(runner.Invocations, i =>
            i.Arguments.Contains($"-C \"{inner}\"", StringComparison.OrdinalIgnoreCase)
            && i.Arguments.Contains("\"src/A/obj\"", StringComparison.Ordinal)
            && i.Arguments.Contains("\"src/C/obj\"", StringComparison.Ordinal));
        Assert.Contains(runner.Invocations, i =>
            i.Arguments.Contains($"-C \"{_repository}\"", StringComparison.OrdinalIgnoreCase)
            && i.Arguments.Contains("\"external/B/obj\"", StringComparison.Ordinal)
            && !i.Arguments.Contains("src/", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>.git</c> Windows will not describe may be the candidate's own repository. Read as absent,
    /// the walk went on to the outer repository, which answered "nothing tracked" about a candidate it
    /// does not hold, and the candidate was never declined. A candidate beside it in the outer
    /// repository is still asked about.
    /// </summary>
    [Fact]
    public async Task DeclinesTheCandidatesUnderAGitFolderWindowsWillNotDescribe()
    {
        var inner = Path.Combine(_repository, "external", "Library");
        var innerGit = Directory.CreateDirectory(Path.Combine(inner, ".git")).FullName;
        var underInner = Path.Combine(inner, "src", "A", "obj");
        var outer = Path.Combine(_repository, "external", "B", "obj");
        var runner = GitListingNothing();

        using var denied = DeniedDirectory.WithUnreadableAttributes(innerGit);

        var findings = await Create(runner).FindTrackedAsync([underInner, outer]);

        Assert.Equal([underInner], findings.Unanswered);
        var asked = Assert.Single(runner.Invocations);
        Assert.Contains($"-C \"{_repository}\"", asked.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"external/B/obj\"", asked.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("src/A/obj", asked.Arguments, StringComparison.Ordinal);
    }

    /// <summary>Git installed and asked leaves nothing unasked, whatever it answered.</summary>
    [Fact]
    public async Task ReportsNothingUnaskedWhenGitIsInstalled()
    {
        var findings = await Create(GitListingNothing()).FindTrackedAsync(Candidates(50));

        Assert.Empty(findings.Unasked);
    }

    /// <summary>
    /// A small repository is still one invocation. Batching must not have turned the common case
    /// into a process per directory.
    /// </summary>
    [Fact]
    public async Task StillCostsOneInvocationForAnOrdinaryRepository()
    {
        var runner = GitListingNothing();

        await Create(runner).FindTrackedAsync(Candidates(50));

        Assert.Single(runner.Invocations);
    }

    private string Pathspec(string candidate) =>
        Path.GetRelativePath(_repository, candidate).Replace(Path.DirectorySeparatorChar, '/');
}
