using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The loop that starts a plan's steps: several at once where they are apart, in the plan's order,
/// within its bound, reported in the plan's order, and stopped as a single step is stopped. Each step
/// here is scripted, so what is proved is the loop and not any removal.
/// </summary>
public sealed class PlanStepsTests
{
    /// <summary>Far longer than a scripted step takes to start, and far shorter than a hung test.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The second step ends first, because the first waits for it. The outcomes are still the plan's
    /// order, and the first could only see the second end if both were under way at once.
    /// </summary>
    [Fact]
    public async Task OutcomesAreReportedInThePlansOrderWhateverOrderTheyEndIn()
    {
        var steps = Apart(2);
        var secondEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = await PlanSteps.RunAsync(
            steps,
            Overlap(steps),
            async (index, _, _) =>
            {
                if (index == 0)
                {
                    await secondEnded.Task.WaitAsync(Patience);
                }
                else
                {
                    secondEnded.SetResult();
                }

                return Outcome(steps[index]);
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal(steps.Select(step => step.Description), run.Outcomes.Select(outcome => outcome.Description));
        Assert.False(run.Interrupted);
    }

    /// <summary>
    /// No more steps are under way than the bound however many are apart (G4), and the bound is
    /// reached: every step is held until the bound's worth have started, which only steps that start
    /// together can do.
    /// </summary>
    [Fact]
    public async Task NoMoreStepsAreUnderWayThanTheBound()
    {
        var steps = Apart(PlanSteps.MostAtOnce * 3);
        using var boundStarted = new CountdownEvent(PlanSteps.MostAtOnce);
        var running = 0;
        var mostAtOnce = 0;

        var run = await PlanSteps.RunAsync(
            steps,
            Overlap(steps),
            (index, _, _) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref mostAtOnce, now);

                if (index < PlanSteps.MostAtOnce)
                {
                    boundStarted.Signal();
                    boundStarted.Wait(Patience);
                }

                Interlocked.Decrement(ref running);
                return Task.FromResult(Outcome(steps[index]));
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal(steps.Count, run.Outcomes.Count);
        Assert.Equal(PlanSteps.MostAtOnce, mostAtOnce);
    }

    /// <summary>
    /// A step that runs alone starts only once every step before it has ended, and every step after it
    /// waits for it to end, even where those steps are apart from each other.
    /// </summary>
    [Fact]
    public async Task AStepThatRunsAloneHasNothingBesideIt()
    {
        CleanupStep[] steps =
        [
            new DeleteDirectoryStep(@"C:\Users\testuser\src\a\obj", "step 0"),
            new DeleteDirectoryStep(@"C:\Users\testuser\src\b\obj", "step 1"),
            new RunCommandStep("tool", "clean", "step 2") { WhenCancelled = CancelledCommand.Ended },
            new DeleteDirectoryStep(@"C:\Users\testuser\src\c\obj", "step 3"),
            new DeleteDirectoryStep(@"C:\Users\testuser\src\d\obj", "step 4"),
        ];

        var running = new HashSet<int>();
        var besideTheCommand = new List<int>();

        await PlanSteps.RunAsync(
            steps,
            Overlap(steps),
            async (index, _, _) =>
            {
                lock (running)
                {
                    if (index == 2 ? running.Count > 0 : running.Contains(2))
                    {
                        besideTheCommand.Add(index);
                    }

                    running.Add(index);
                }

                await Task.Delay(20);

                lock (running)
                {
                    running.Remove(index);
                }

                return Outcome(steps[index]);
            },
            progress: null,
            CancellationToken.None);

        Assert.Empty(besideTheCommand);
    }

    /// <summary>
    /// Cancellation stops new steps from starting. The steps under way are left to stop as they do
    /// alone, are waited for, and are reported; the steps never started are not.
    /// </summary>
    [Fact]
    public async Task CancellationStopsNewStepsAndWaitsForThoseUnderWay()
    {
        var steps = Apart(PlanSteps.MostAtOnce + 4);
        using var cts = new CancellationTokenSource();
        using var allStarted = new CountdownEvent(PlanSteps.MostAtOnce);
        var started = 0;
        var toldToStop = 0;

        var run = await PlanSteps.RunAsync(
            steps,
            Overlap(steps),
            async (index, _, stop) =>
            {
                Interlocked.Increment(ref started);
                allStarted.Signal();

                if (allStarted.Wait(Patience) && index == 0)
                {
                    await cts.CancelAsync();
                }

                // A step stops where the clean is cancelled, as a removal does, and says so. Bounded, so
                // a loop that never told it to stop fails the test rather than hanging it.
                if (await ToldToStop(stop))
                {
                    Interlocked.Increment(ref toldToStop);
                }

                return Outcome(steps[index]) with { Interrupted = true };
            },
            progress: null,
            cts.Token);

        Assert.True(run.Interrupted);
        Assert.Equal(PlanSteps.MostAtOnce, started);
        Assert.Equal(PlanSteps.MostAtOnce, toldToStop);
        Assert.Equal(PlanSteps.MostAtOnce, run.Outcomes.Count);
        Assert.All(run.Outcomes, outcome => Assert.True(outcome.Interrupted));
    }

    /// <summary>
    /// A step can run to its end though the clean was cancelled while it ran, as a tool's command left
    /// to finish does. Its outcome is not a stop, and the step after it still never starts.
    /// </summary>
    [Fact]
    public async Task AStepThatFinishesAfterTheCleanIsCancelledStartsNoOther()
    {
        CleanupStep[] steps =
        [
            new RunCommandStep("tool", "clean", "step 0") { WhenCancelled = CancelledCommand.Finishes },
            new RunCommandStep("tool", "prune", "step 1") { WhenCancelled = CancelledCommand.Finishes },
        ];

        using var cts = new CancellationTokenSource();
        var started = new List<int>();

        var run = await PlanSteps.RunAsync(
            steps,
            Overlap(steps),
            async (index, _, _) =>
            {
                lock (started)
                {
                    started.Add(index);
                }

                await cts.CancelAsync();
                return Outcome(steps[index]);
            },
            progress: null,
            cts.Token);

        Assert.Equal([0], started);
        Assert.True(run.Interrupted);
        Assert.False(Assert.Single(run.Outcomes).Interrupted);
    }

    /// <summary>
    /// A step that throws stops the plan. The steps beside it are told to stop and waited for before the
    /// exception reaches the caller, unchanged, because a removal still running then would be acting on
    /// the disk with nobody left to report it.
    /// </summary>
    [Fact]
    public async Task AStepThatThrowsStopsThoseBesideItBeforeTheExceptionIsThrown()
    {
        var steps = Apart(PlanSteps.MostAtOnce + 4);
        using var allStarted = new CountdownEvent(PlanSteps.MostAtOnce);
        var thrown = new InvalidOperationException("The step failed.");
        var started = 0;
        var stillRunning = 0;
        var toldToStop = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => PlanSteps.RunAsync(
            steps,
            Overlap(steps),
            async (index, _, stop) =>
            {
                Interlocked.Increment(ref started);
                Interlocked.Increment(ref stillRunning);
                allStarted.Signal();
                allStarted.Wait(Patience);

                try
                {
                    if (index == 0)
                    {
                        throw thrown;
                    }

                    if (await ToldToStop(stop))
                    {
                        Interlocked.Increment(ref toldToStop);
                    }

                    return Outcome(steps[index]) with { Interrupted = true };
                }
                finally
                {
                    Interlocked.Decrement(ref stillRunning);
                }
            },
            progress: null,
            CancellationToken.None));

        Assert.Same(thrown, failure);
        Assert.Equal(0, Volatile.Read(ref stillRunning));
        Assert.Equal(PlanSteps.MostAtOnce - 1, toldToStop);
        Assert.Equal(PlanSteps.MostAtOnce, started);
    }

    /// <summary>
    /// The bar is every ended step's weight and each running step's own fraction of its weight, and it
    /// never goes backwards, however the steps' reports interleave.
    /// </summary>
    [Fact]
    public async Task TheBarIsTheSumOfTheStepsAndNeverGoesBackwards()
    {
        var steps = Apart(PlanSteps.MostAtOnce);
        var reports = new List<double>();
        var bar = new CallbackProgress<double>(value =>
        {
            lock (reports)
            {
                reports.Add(value);
            }
        });

        await PlanSteps.RunAsync(
            steps,
            Overlap(steps),
            (index, progress, _) =>
            {
                for (var tenth = 1; tenth <= 10; tenth++)
                {
                    progress!.Report(tenth / 10.0);
                }

                // A report lower than one already made is never shown.
                progress!.Report(0.5);

                return Task.FromResult(Outcome(steps[index]));
            },
            bar,
            CancellationToken.None);

        Assert.Equal(1.0, reports[^1], precision: 9);
        Assert.All(reports.Zip(reports.Skip(1)), pair => Assert.True(pair.Second >= pair.First, $"{pair.Second} followed {pair.First}"));
    }

    /// <summary>
    /// Wait until <paramref name="stop"/> is cancelled or the patience runs out, and say which. Never
    /// thrown, so a step that was not told to stop ends and is counted rather than hanging the test.
    /// </summary>
    private static async Task<bool> ToldToStop(CancellationToken stop)
    {
        await Task.Delay(Patience, stop).ContinueWith(static _ => { }, TaskScheduler.Default);
        return stop.IsCancellationRequested;
    }

    private static IReadOnlyList<CleanupStep> Apart(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new DeleteDirectoryStep($@"C:\Users\testuser\src\p{index}\obj", $"step {index}"))];

    private static StepOverlap Overlap(IReadOnlyList<CleanupStep> steps) =>
        StepOverlap.Of(steps, new FakeVolumeInventory(), EmptyFileSystem.Instance);

    private static StepOutcome Outcome(CleanupStep step) =>
        new(step.Description, Succeeded: true, BytesReclaimed: 0, Refusals.None, "Removed.");

    private static void InterlockedMax(ref int target, int value)
    {
        for (var most = Volatile.Read(ref target); value > most; most = Volatile.Read(ref target))
        {
            Interlocked.CompareExchange(ref target, value, most);
        }
    }
}
