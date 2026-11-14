using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Each plan a clean reaches is reported to its caller before its first step, with what the run does
/// with it and the span of the bar it accounts for, so the Storage list can follow the run to the row
/// it is on and show how far through that row it is.
/// </summary>
public sealed class CleanupPlannerStartedTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The plans are reported in the order they run, each before it starts, and their spans lie end to
    /// end across the bar. The plan that only proves comes last and takes no share, as it does of the bar.
    /// </summary>
    [Fact]
    public async Task EachPlanIsReportedBeforeItRunsWithItsSpanOfTheBar()
    {
        var started = new ProgressRecorder<PlanUnderway>();
        var reportedBeforeFirstCleaned = -1;

        var first = new FakeCleanupProvider("first")
        {
            Steps = [Step(_temp.CreateDirectory("first"), 2_000)],
            AfterCleaning = () => reportedBeforeFirstCleaned = started.Reports.Count,
        };
        var second = new FakeCleanupProvider("second") { Steps = [Step(_temp.CreateDirectory("second"), 1_000)] };
        var proof = new FakeCleanupProvider("proof")
        {
            ProtectedPaths = [new ProtectedPath(_temp.CreateDirectory("kept"), "On the keep list.", PathPresence.Present, Withheld: Withholding.OnKeepList)],
        };

        var planner = new CleanupPlanner([first, second, proof]);
        await planner.ExecuteAsync(await planner.PlanAllAsync(), started: started);

        Assert.Equal(1, reportedBeforeFirstCleaned);
        Assert.Equal(
            [
                ("first", PlanStage.Removing, 0.0, 0.666667),
                ("second", PlanStage.Removing, 0.666667, 0.333333),
                ("proof", PlanStage.Checking, 1.0, 0.0),
            ],
            started.Reports.Select(plan => (plan.Provider.Id, plan.Stage, Math.Round(plan.From, 6), Math.Round(plan.Share, 6))));
    }

    /// <summary>
    /// A plan the clean was cancelled before starting is still reported, as only checked: its §5.6
    /// proof takes time on that row, and the row is not being cleaned.
    /// </summary>
    [Fact]
    public async Task APlanACancelledRunNeverStartedIsReportedAsChecked()
    {
        using var cts = new CancellationTokenSource();
        var first = new FakeCleanupProvider("first")
        {
            Steps = [Step(_temp.CreateDirectory("first"), 2_000)],
            AfterCleaning = cts.Cancel,
        };
        var second = new FakeCleanupProvider("second") { Steps = [Step(_temp.CreateDirectory("second"), 1_000)] };
        var started = new ProgressRecorder<PlanUnderway>();

        var planner = new CleanupPlanner([first, second]);
        await planner.ExecuteAsync(await planner.PlanAllAsync(), started: started, ct: cts.Token);

        Assert.Equal(
            [("first", PlanStage.Removing), ("second", PlanStage.Checking)],
            started.Reports.Select(plan => (plan.Provider.Id, plan.Stage)));
        Assert.Equal(0, started.Reports[1].Share);
        Assert.Empty(second.Executed);
    }

    private static DeleteDirectoryStep Step(string path, long bytes) =>
        new(path, Path.GetFileName(path)) { Estimated = new ScanSize(bytes, bytes) };
}
