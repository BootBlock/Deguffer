using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

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
        _environment = new FakeUserEnvironment(_temp.Path);
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

    private TrackedFileCheck Create(FakeProcessRunner runner) =>
        new(_environment.WithExecutable("git"), runner);

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
        var runner = new FakeProcessRunner().Replying(arguments =>
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
        var runner = new FakeProcessRunner();

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
        var runner = new FakeProcessRunner();

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

        var runner = new FakeProcessRunner().Replying(arguments =>
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
    /// on the strength of a question that was never asked.
    /// </summary>
    [Fact]
    public async Task AsksNothingAndDeclinesNothingWhenGitIsNotInstalled()
    {
        var runner = new FakeProcessRunner();

        var findings = await new TrackedFileCheck(new FakeUserEnvironment(_temp.Path), runner)
            .FindTrackedAsync(Candidates(900));

        Assert.Empty(runner.Invocations);
        Assert.Empty(findings.Tracked);
        Assert.Empty(findings.Unanswered);
    }

    /// <summary>
    /// A small repository is still one invocation. Batching must not have turned the common case
    /// into a process per directory.
    /// </summary>
    [Fact]
    public async Task StillCostsOneInvocationForAnOrdinaryRepository()
    {
        var runner = new FakeProcessRunner();

        await Create(runner).FindTrackedAsync(Candidates(50));

        Assert.Single(runner.Invocations);
    }

    private string Pathspec(string candidate) =>
        Path.GetRelativePath(_repository, candidate).Replace(Path.DirectorySeparatorChar, '/');
}
