using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A preview plans several providers at once, so a provider waiting on its tool does not hold up the
/// providers after it, and what the caller sees of the pass is still orderly: one report at a time,
/// ties in the providers' order, and a status line that names only what is still running.
/// </summary>
public sealed class CleanupPlannerConcurrencyTests
{
    /// <summary>Far longer than two providers take to start together, and far shorter than a hung test.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Each provider waits, as one waiting on its tool does, until the other has started. Planned one
    /// at a time, the first would wait for a provider that cannot start until it ends.
    /// </summary>
    [Fact]
    public async Task APassPlansAProviderWhileAnotherIsStillWaiting()
    {
        using var bothStarted = new CountdownEvent(2);
        var firstPlan = new StartedTogether(bothStarted);
        var secondPlan = new StartedTogether(bothStarted);

        var findings = await new CleanupPlanner(
            [
                new FakeCleanupProvider("first") { WhilePlanning = firstPlan.Arrive },
                new FakeCleanupProvider("second") { WhilePlanning = secondPlan.Arrive },
            ])
            .PlanAllAsync()
            .WaitAsync(Deadline * 2);

        Assert.Equal(2, findings.Count);
        Assert.True(firstPlan.SawTheOther, "The first provider was planned alone: the second had not started.");
        Assert.True(secondPlan.SawTheOther, "The second provider was planned alone: the first had not started.");
    }

    /// <summary>
    /// A pass holds no more providers at once than its bound (G4), however many it has, and more than
    /// one. More than one is the floor rather than the bound itself, because a machine with few threads
    /// to spare may not start all of them inside a provider's few milliseconds. The floor is proven by
    /// order: the first provider is held until a second has begun.
    ///
    /// <para>Only a machine with more logical processors than the bound can show the bound being
    /// passed: with no bound given, <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
    /// runs one body per processor. On a smaller machine this test still proves that providers ran
    /// together.</para>
    /// </summary>
    [Fact]
    public async Task APassPlansNoMoreProvidersAtOnceThanItsBound()
    {
        var planning = new HeldOverlap(Deadline);
        var providers = Enumerable.Range(0, CleanupPlanner.ProvidersAtOnce * 3)
            .Select(index => new FakeCleanupProvider($"p{index}")
            {
                WhilePlanning = () =>
                {
                    planning.Begin();

                    // Long enough for every provider the bound admits to be under way at once.
                    Thread.Sleep(50);
                    planning.End();
                },
            })
            .ToList();

        await new CleanupPlanner(providers).PlanAllAsync().WaitAsync(Deadline * 2);

        Assert.InRange(planning.MostAtOnce, 2, CleanupPlanner.ProvidersAtOnce);
    }

    /// <summary>
    /// The caller's handler for each finding is never run beside itself. Providers that finish together
    /// would otherwise report together, and a handler written for the one-at-a-time pass, as the
    /// preview's is, would insert two rows into one list at once.
    /// </summary>
    [Fact]
    public async Task FindingsAreReportedOneAtATimeThoughProvidersFinishTogether()
    {
        var found = new OverlapRecorder();
        var providers = Enumerable.Range(0, CleanupPlanner.ProvidersAtOnce)
            .Select(index => new FakeCleanupProvider($"p{index}"))
            .ToList();

        await new CleanupPlanner(providers).PlanAllAsync(found: found).WaitAsync(Deadline);

        Assert.Equal(providers.Count, found.Reports);
        Assert.Equal(1, found.Peak);
    }

    /// <summary>
    /// Findings of equal size are returned in the providers' order, whatever order they finished in, so
    /// the same machine gives the same list on every pass.
    /// </summary>
    [Fact]
    public async Task FindingsOfEqualSizeAreReturnedInTheProvidersOrder()
    {
        var providers = new[] { ("a", 0L), ("b", 500L), ("c", 0L), ("d", 500L) }
            .Select(row => Sized(row.Item1, row.Item2))
            .ToList();

        // Each provider finishes after the ones listed after it, the reverse of the planner's order.
        var finishing = new FinishingOrder([.. Enumerable.Reverse(providers)]);

        var findings = await new CleanupPlanner(providers).PlanAllAsync(found: finishing).WaitAsync(Deadline);

        Assert.Equal(["b", "d", "a", "c"], findings.Select(finding => finding.Provider.Id));
    }

    /// <summary>
    /// The status line names every provider still being checked, and stops naming one as soon as it
    /// ends, so it never names a provider that has finished while another is still running. The line is
    /// posted by time, so the clock is moved an interval at a time, and nothing is posted after the pass.
    /// </summary>
    [Fact]
    public async Task TheStatusNamesTheProvidersStillBeingCheckedAndDropsOneThatEnded()
    {
        var clock = new ManualTimeProvider();
        using var bothStarted = new CountdownEvent(2);
        using var quickEnds = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var status = new ProgressRecorder<string>();

        var quick = new FakeCleanupProvider("quick")
        {
            WhilePlanning = () =>
            {
                bothStarted.Signal();
                quickEnds.Wait(Deadline);
            },
        };
        var slow = new FakeCleanupProvider("slow")
        {
            WhilePlanning = () =>
            {
                bothStarted.Signal();
                release.Wait(Deadline);
            },
        };

        var pass = new CleanupPlanner([slow, quick], time: clock).PlanAllAsync(status: status);

        // The first start is posted at once, and the second, made inside the interval, at its end.
        Assert.True(bothStarted.Wait(Deadline), "The two providers were never under way together.");
        await clock.WhenWaitingAsync(Deadline);
        clock.Advance(ReportCadence.Interval);

        quickEnds.Set();
        await clock.WhenWaitingAsync(Deadline);
        clock.Advance(ReportCadence.Interval);

        var whileSlowRan = status.Reports;
        release.Set();
        await pass.WaitAsync(Deadline);
        clock.Advance(ReportCadence.Interval);

        // Which of the two started first is the scheduler's choice, and either is a true line. The first
        // start's line is read as it is posted, so where the second start came first it names both, and
        // the line due at the interval's end says nothing new.
        Assert.InRange(whileSlowRan.Count, 2, 3);
        Assert.True(
            whileSlowRan[^2] is "Checking slow and quick…" or "Checking quick and slow…",
            $"While both ran, the line was '{whileSlowRan[^2]}'.");
        Assert.Equal("Checking slow…", whileSlowRan[^1]);

        // Nothing is said once the last one ends: the caller's own sentence replaces the line.
        Assert.Equal(whileSlowRan, status.Reports);
    }

    /// <summary>
    /// A provider that fails stops being named, so the line does not go on saying it is being checked
    /// while the pass that failed with it ends.
    /// </summary>
    [Fact]
    public async Task AProviderThatFailsStopsBeingNamed()
    {
        var clock = new ManualTimeProvider();
        var status = new ProgressRecorder<string>();
        using var waitingStarted = new ManualResetEventSlim();
        using var failingStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var failing = new FakeCleanupProvider("failing")
        {
            WhilePlanning = () =>
            {
                failingStarted.Set();
                waitingStarted.Wait(Deadline);
            },
            PlanFailure = new InvalidOperationException("The tool would not answer."),
        };

        var waiting = new FakeCleanupProvider("waiting")
        {
            // Holds the pass open until the failure has been reported, so the line is read after it.
            WhilePlanning = () =>
            {
                waitingStarted.Set();
                release.Wait(Deadline);
            },
        };

        var pass = new CleanupPlanner([waiting, failing], time: clock).PlanAllAsync(status: status);

        Assert.True(failingStarted.Wait(Deadline) && waitingStarted.Wait(Deadline), "The two providers were never under way together.");

        // Each line due is posted until the line names only the provider still running and nothing more
        // is due. A line that went on naming the failed provider would never settle, and the clock would
        // wait for a report nobody arms.
        while (status.Reports is not [.., "Checking waiting…"] || clock.Waiting > 0)
        {
            await clock.WhenWaitingAsync(Deadline);
            clock.Advance(ReportCadence.Interval);
        }

        release.Set();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pass.WaitAsync(Deadline));
        Assert.Equal("Checking waiting…", status.Reports[^1]);
    }

    private static FakeCleanupProvider Sized(string id, long bytes) =>
        new(id)
        {
            Steps = bytes == 0 ? [] : [new DeleteDirectoryStep(@$"C:\Users\testuser\{id}", "Output") { Estimated = new ScanSize(bytes, bytes) }],
        };

    private static void InterlockedMax(ref int peak, int now)
    {
        var seen = Volatile.Read(ref peak);
        while (now > seen)
        {
            var was = Interlocked.CompareExchange(ref peak, now, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    /// <summary>A provider's plan waiting, as one waiting on a tool does, until another provider has started.</summary>
    private sealed class StartedTogether(CountdownEvent bothStarted)
    {
        public bool SawTheOther { get; private set; }

        public void Arrive()
        {
            bothStarted.Signal();
            SawTheOther = bothStarted.Wait(Deadline);
        }
    }

    /// <summary>Records findings, and the most handlers that ever ran at once.</summary>
    private sealed class OverlapRecorder : IProgress<Finding>
    {
        private int _running;
        private int _peak;
        private int _reports;

        public int Peak => Volatile.Read(ref _peak);

        public int Reports => Volatile.Read(ref _reports);

        public void Report(Finding value)
        {
            InterlockedMax(ref _peak, Interlocked.Increment(ref _running));
            Interlocked.Increment(ref _reports);

            // Long enough for a second report to arrive while this one runs, where nothing stops it.
            Thread.Sleep(30);
            Interlocked.Decrement(ref _running);
        }
    }
}
