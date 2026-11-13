using System.Runtime.InteropServices;
using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Timing;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The plan route (#443): asked for with no target, it runs a planner's whole pass, and the report
/// states the pass and when each provider took a slot and ended, by id and never by place.
/// </summary>
public sealed class PlanRouteTests
{
    private static readonly MachineFacts Machine = new(
        16,
        new Version(10, 0, 26100),
        new Version(10, 0, 0),
        Architecture.X64,
        Elevated: false,
        DebugBuild: false,
        new CollectorFacts(Server: false, Concurrent: true, Gen0Size: null));

    [Fact]
    public void ThePlanRouteTakesNoTarget()
    {
        var request = BenchmarkRequest.Parse(["plan"], out var error);

        Assert.Null(error);
        Assert.Equal(Route.Plan, request!.Route);
        Assert.Empty(request.Folders);
        Assert.Equal(BenchmarkRequest.DefaultRuns, request.Runs);
    }

    [Fact]
    public void ThePlanRouteTakesARunCount()
    {
        var request = BenchmarkRequest.Parse(["plan", "--runs", "12"], out var error);

        Assert.Null(error);
        Assert.Equal(12, request!.Runs);
    }

    /// <summary>A target or another route's option would be ignored, so both are refused rather than timed as something else.</summary>
    [Theory]
    [InlineData("plan", @"C:\Users\testuser")]
    [InlineData("plan", "--threads", "4")]
    [InlineData("plan", "--read-size", "64")]
    public void ThePlanRouteRefusesATargetAndAnotherRoutesOptions(params string[] args)
    {
        Assert.Null(BenchmarkRequest.Parse(args, out var error));
        Assert.NotNull(error);
    }

    /// <summary>Every other route still needs its target.</summary>
    [Fact]
    public void AnotherRouteStillNeedsATarget()
    {
        Assert.Null(BenchmarkRequest.Parse(["walk"], out var error));
        Assert.NotNull(error);
    }

    /// <summary>The route plans every provider of its planner once a run, and keeps each one's time.</summary>
    [Fact]
    public async Task ARunPlansEveryProviderAndTimesEach()
    {
        var providers = new[] { new FakeCleanupProvider("npm"), new FakeCleanupProvider("conda") };

        var tally = await new PlanRoute(new CleanupPlanner(providers)).Timed.Run(CancellationToken.None);

        Assert.Equal(2, tally.Items);
        Assert.True(tally.Complete);
        Assert.Equal(["conda", "npm"], tally.Planning!.Times.Select(time => time.Provider.Id).Order());
    }

    /// <summary>
    /// Each provider's start, end and time, in the order the first run started them, with the later
    /// runs' medians beside them, and the bound stated with the tuning.
    /// </summary>
    [Fact]
    public void TheReportStatesEachProvidersStartEndAndTime()
    {
        var npm = new FakeCleanupProvider("npm");
        var conda = new FakeCleanupProvider("conda");

        var text = Report.Render(
            Route.Plan,
            Tuning.Describe(Route.Plan, WalkTuning.Default, BenchmarkRequest.UnknownDrive, snapshots: false, LinkRead.Name, BenchmarkRequest.DefaultTree, trees: 1),
            [],
            Machine,
            [
                Pass(7, (conda, 0.002, 5.232), (npm, 0.010, 0.130)),
                Pass(5, (npm, 0.001, 0.101), (conda, 0.001, 4.001)),
                Pass(6, (npm, 0.003, 0.203), (conda, 0.003, 4.503)),
            ]);

        var lines = text.Split('\n');
        Assert.Contains(lines, line => line.StartsWith("tuning", StringComparison.Ordinal) && line.Contains($"{CleanupPlanner.ProvidersAtOnce} providers at once", StringComparison.Ordinal));
        Assert.Contains("not elevated", text, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.StartsWith("place", StringComparison.Ordinal));
        Assert.DoesNotContain("providers/s", text, StringComparison.Ordinal);

        var conda0 = Array.FindIndex(lines, line => line.StartsWith("conda ", StringComparison.Ordinal));
        var npm0 = Array.FindIndex(lines, line => line.StartsWith("npm ", StringComparison.Ordinal));
        Assert.True(conda0 > 0 && npm0 == conda0 + 1, "The providers are listed in the order the first run started them.");

        Assert.Equal(["conda", "2.0 ms", "5.232 s", "5.230 s", "2.0 ms", "4.252 s", "4.250 s"], Columns(lines[conda0]));
        Assert.Equal(["npm", "10.0 ms", "130.0 ms", "120.0 ms", "2.0 ms", "152.0 ms", "150.0 ms"], Columns(lines[npm0]));
    }

    /// <summary>One run has no later runs, so a provider's row has only the first run's columns.</summary>
    [Fact]
    public void OneRunHasOnlyTheFirstRunsColumns()
    {
        var npm = new FakeCleanupProvider("npm");

        var text = Report.Render(Route.Plan, "tuning", [], Machine, [Pass(1, (npm, 0, 0.5))]);

        var row = text.Split('\n').Single(line => line.StartsWith("npm ", StringComparison.Ordinal));
        Assert.Equal(["npm", "0.0 ms", "500.0 ms", "500.0 ms"], Columns(row));
        Assert.DoesNotContain("later runs", text, StringComparison.Ordinal);
    }

    private static RunSample Pass(double seconds, params (FakeCleanupProvider Provider, double Began, double Ended)[] times) =>
        new(
            TimeSpan.FromSeconds(seconds),
            new RunTally(
                times.Length,
                BytesRead: 0,
                Complete: true,
                Planning: new PlanRun(
                [
                    .. times.Select(time => new ProviderPlanTime(time.Provider, TimeSpan.FromSeconds(time.Began), TimeSpan.FromSeconds(time.Ended))),
                ])),
            AllocatedBytes: 0,
            FullCollections: 0,
            PeakWorkingSet: 0,
            ProcessorTime: TimeSpan.Zero);

    /// <summary>A row's id and its times, each a number and its unit.</summary>
    private static string[] Columns(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [words[0], .. words.Skip(1).Chunk(2).Select(pair => string.Join(' ', pair))];
    }
}
