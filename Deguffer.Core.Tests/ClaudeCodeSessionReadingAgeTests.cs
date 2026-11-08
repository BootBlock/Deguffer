using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using static Deguffer.Testing.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// How fresh a clean's Claude Code checks read the list of running sessions: the clean's own reading,
/// never the preview's, and no older than <see cref="IUseCheck.ReadingLimit"/>.
///
/// <para>Each step's check used to read the whole list again, so a clean of thousands of shell
/// captures, rewind snapshots and unsent events listed the folder, parsed every entry and probed every
/// entry's process once per step. Every reading here is counted by the probes of one entry whose
/// process has ended, which the list asks about each time it is read.</para>
/// </summary>
public sealed class ClaudeCodeSessionReadingAgeTests : IDisposable
{
    private const int EndedProcess = 7001;
    private const int ResumedProcess = 4242;
    private const string OrderingTool = "tool";
    private const string OrderingCommand = "noop";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ClaudeCodeFixture _claude;
    private readonly FakeProcessInspector _inspector = FakeProcessInspector.NothingRunning;
    private readonly ManualTimeProvider _clock = new();
    private readonly ClaudeCodeSessionRegistry _sessions;

    public ClaudeCodeSessionReadingAgeTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _claude = new ClaudeCodeFixture(_environment);
        _sessions = new ClaudeCodeSessionRegistry(_environment, _inspector, time: _clock);
        _claude.Registered(EndedProcess, SessionC);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The cost the bound removes, through the leftovers row as the planner builds it. The clean reads
    /// the list once for every step, and once more than the preview did, because it never answers
    /// from the preview's reading.
    /// </summary>
    [Fact]
    public async Task EveryCheckOfACleanWithinTheLimitIsAnsweredFromOneReading()
    {
        var events = Enumerable.Range(0, 6).Select(_ => _claude.FailedEvents(Guid.NewGuid().ToString(), age: Old)).ToList();

        var provider = new ClaudeCodeDerivedStateProvider(
            _environment, sessions: _sessions, runner: new FakeProcessRunner(), inspector: _inspector);
        var plan = await provider.PlanAsync();

        Assert.All(events, path => Assert.Contains(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase));
        var preview = _inspector.ProbeCount(EndedProcess);
        Assert.True(preview > 0, "the preview never read the list, so the count below proves nothing");

        var result = await provider.ExecuteAsync(plan);

        Assert.All(events, path => Assert.False(File.Exists(path), $"{path} was kept, so the checks did not all run"));
        Assert.Equal(preview + 1, _inspector.ProbeCount(EndedProcess));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The guarantee the bound keeps, and the §5.6 negative. The clean's first check reads the list
    /// with nothing running, a session is resumed, and the clean's next check comes later than the
    /// limit. It reads again, so the resumed session's events stay, and the ended session's events
    /// before it are still removed.
    /// </summary>
    [Fact]
    public async Task AReadingPastTheLimitIsReadAgainSoASessionResumedSinceIsSeen()
    {
        var ended = _claude.FailedEvents(SessionB, age: Old);
        var resumed = _claude.FailedEvents(SessionA, age: Old);

        var result = await Execute(
            Removal(ended, SessionB),
            Then(),
            Meanwhile(() =>
            {
                Resume(SessionA);
                _clock.Advance(IUseCheck.ReadingLimit + TimeSpan.FromTicks(1));
            }),
            Then(),
            Removal(resumed, SessionA));

        Assert.False(File.Exists(ended), "an ended session's events were kept");
        Assert.True(File.Exists(resumed), "the events of a session resumed during the clean were removed");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: the Claude Code session it belongs to is running again.");
        Assert.Equal(2, _inspector.ProbeCount(EndedProcess));
        AssertProvedStanding(result, resumed);
    }

    /// <summary>
    /// A list that could not be read in full is a reading like any other: every check within the limit
    /// holds its step back on it, even after the list could be read again.
    /// </summary>
    [Fact]
    public async Task AListThatCouldNotBeReadHoldsBackEveryStepWithinTheLimit()
    {
        var first = _claude.FailedEvents(SessionA, age: Old);
        var second = _claude.FailedEvents(SessionB, age: Old);
        var unreadable = _claude.WriteText(Path.Combine(_claude.Sessions, "5150.json"), "not json");

        var result = await Execute(
            Removal(first, SessionA),
            Then(),
            Meanwhile(() => File.Delete(unreadable)),
            Then(),
            Removal(second, SessionB));

        Assert.True(File.Exists(first), "events were removed on a list nobody could read");
        Assert.True(File.Exists(second), "events were removed within the limit of a list nobody could read");
        Assert.Equal(
            2,
            result.Steps.Count(step => step.Message?.StartsWith(
                "Nothing was removed: Deguffer could not read Claude Code's list of running sessions",
                StringComparison.Ordinal) == true));
        AssertProvedStanding(result, first);
        AssertProvedStanding(result, second);
    }

    /// <summary>
    /// The limit is the oldest a reading may be, not a moment before it: one exactly as old answers,
    /// and one a tick older is read again.
    /// </summary>
    [Fact]
    public void AReadingIsKeptUpToTheLimitAndReadAgainPastIt()
    {
        var first = _sessions.ReadNoOlderThan(IUseCheck.ReadingLimit);

        _clock.Advance(IUseCheck.ReadingLimit);
        Assert.Same(first, _sessions.ReadNoOlderThan(IUseCheck.ReadingLimit));
        Assert.Equal(1, _inspector.ProbeCount(EndedProcess));

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.NotSame(first, _sessions.ReadNoOlderThan(IUseCheck.ReadingLimit));
        Assert.Equal(2, _inspector.ProbeCount(EndedProcess));
    }

    /// <summary>
    /// The planning pass's reading never answers a clean, however young: a preview can sit on screen
    /// indefinitely, and its reading is the answer the clean's check exists to replace.
    /// </summary>
    [Fact]
    public void TheCleanNeverAnswersFromThePlanningPassReading()
    {
        Assert.Empty(_sessions.Read().Live);

        Resume(SessionA);

        Assert.True(_sessions.ReadNoOlderThan(IUseCheck.ReadingLimit).Lists(SessionA));
        Assert.False(_sessions.Read().Lists(SessionA));
    }

    /// <summary>The user resumes <paramref name="session"/> while the clean runs.</summary>
    private void Resume(string session)
    {
        _claude.Registered(ResumedProcess, session);
        _inspector.WithProcess(ResumedProcess, ProcessLiveness.Running(null));
    }

    private DeleteFileStep Removal(string path, string session) =>
        new(path, "Unsent usage events") { UseCheck = ClaudeCodeSessionCheck.Ended(_sessions, session) };

    /// <summary>
    /// A step between two others whose check changes the machine, so what changes during a clean is
    /// placed between two of its checks. Its own file is removed like any other.
    /// </summary>
    private DeleteFileStep Meanwhile(Action change) =>
        new(_temp.CreateFile(16, "between", $"{Guid.NewGuid():N}.bin"), "A file between two checks")
        {
            UseCheck = new ChangingCheck(change),
        };

    /// <summary>
    /// A tool's command between two steps. It runs alone, so the step before it has ended before the
    /// step after it is asked anything: removals of files apart would otherwise start together, and the
    /// order the checks are asked in, which these tests are about, would be the scheduler's.
    /// </summary>
    private static RunCommandStep Then() =>
        new(OrderingTool, OrderingCommand, "A command between two steps") { WhenCancelled = CancelledCommand.Ended };

    private Task<CleanupResult> Execute(params IReadOnlyList<CleanupStep> steps) =>
        new PlanExecutor(
                new FakeProcessRunner().Responding(OrderingTool, OrderingCommand, string.Empty),
                ParallelEnumerationScanner.Default,
                RefusalRecord.For(_environment))
            .ExecuteAsync(
                new CleanupPlan
                {
                    ProviderId = "test",
                    ProviderName = "Test",
                    Tier = SafetyTier.RegenerableCache,
                    WhatHappensOnNextUse = "Nothing.",
                    Steps = steps,
                },
                runReach: null,
                residue: null,
                progress: null,
                underway: null,
                ct: CancellationToken.None);

    private static void AssertProvedStanding(CleanupResult result, string path)
    {
        var check = Assert.Single(result.Verification!.Checks, c => c.Subject.Equals(path, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(VerificationOutcome.Survived, check.Outcome);
        Assert.True(result.Verification.Passed, result.Verification.Summary);
    }

    private sealed class ChangingCheck(Action change) : IUseCheck
    {
        public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
        {
            change();
            return [];
        }
    }
}
