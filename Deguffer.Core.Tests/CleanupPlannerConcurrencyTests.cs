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
    /// A pass holds no more providers at once than its bound (G4), however many it has. More than one is
    /// the floor rather than the bound itself, because a machine with few threads to spare may not
    /// start all of them inside a provider's few milliseconds.
    ///
    /// <para>Only a machine with more logical processors than the bound can show the bound being
    /// passed: with no bound given, <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
    /// runs one body per processor. On a smaller machine this test still proves that providers ran
    /// together.</para>
    /// </summary>
    [Fact]
    public async Task APassPlansNoMoreProvidersAtOnceThanItsBound()
    {
        var inFlight = 0;
        var peak = 0;
        var providers = Enumerable.Range(0, CleanupPlanner.ProvidersAtOnce * 3)
            .Select(index => new FakeCleanupProvider($"p{index}")
            {
                WhilePlanning = () =>
                {
                    var now = Interlocked.Increment(ref inFlight);
                    InterlockedMax(ref peak, now);

                    // Long enough for every provider the bound admits to be under way at once.
                    Thread.Sleep(50);
                    Interlocked.Decrement(ref inFlight);
                },
            })
            .ToList();

        await new CleanupPlanner(providers).PlanAllAsync().WaitAsync(Deadline);

        Assert.InRange(Volatile.Read(ref peak), 2, CleanupPlanner.ProvidersAtOnce);
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
        // Each provider finishes after the ones listed after it, the reverse of the planner's order.
        var providers = new[] { ("a", 0L), ("b", 500L), ("c", 0L), ("d", 500L) }
            .Select((row, index) => Sized(row.Item1, row.Item2, finishAfter: TimeSpan.FromMilliseconds(40 * (4 - index))))
            .ToList();

        var findings = await new CleanupPlanner(providers).PlanAllAsync().WaitAsync(Deadline);

        Assert.Equal(["b", "d", "a", "c"], findings.Select(finding => finding.Provider.Id));
    }

    /// <summary>
    /// The status line names every provider still being checked, and stops naming one as soon as it
    /// ends, so it never names a provider that has finished while another is still running.
    /// </summary>
    [Fact]
    public async Task TheStatusNamesTheProvidersStillBeingCheckedAndDropsOneThatEnded()
    {
        using var release = new ManualResetEventSlim();
        using var bothStarted = new CountdownEvent(2);
        var status = new ProgressRecorder<string>();

        var quick = new FakeCleanupProvider("quick")
        {
            WhilePlanning = () =>
            {
                bothStarted.Signal();
                bothStarted.Wait(Deadline);
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

        var pass = new CleanupPlanner([slow, quick]).PlanAllAsync(status: status);

        Assert.True(bothStarted.Wait(Deadline), "The two providers were never under way together.");
        SpinWait.SpinUntil(() => status.Reports.Count >= 3, Deadline);
        var whileSlowRan = status.Reports;
        release.Set();
        await pass.WaitAsync(Deadline);

        // Which of the two started first is the scheduler's choice, and either is a true line.
        Assert.Equal(3, whileSlowRan.Count);
        Assert.True(
            whileSlowRan[1] is "Checking slow and quick…" or "Checking quick and slow…",
            $"While both ran, the line was '{whileSlowRan[1]}'.");
        Assert.Equal("Checking slow…", whileSlowRan[2]);

        // Nothing is said once the last one ends: the caller's own sentence replaces the line.
        Assert.Equal(whileSlowRan, status.Reports);
    }

    [Fact]
    public void TheStatusOfManyProvidersNamesTheFirstTwoAndCountsTheRest()
    {
        ICleanupProvider[] checking = [.. new[] { "conda", "pip", "npm", "PlatformIO" }.Select(name => new FakeCleanupProvider(name))];

        Assert.Equal("Checking conda…", PlanningStatus.Describe(checking[..1]));
        Assert.Equal("Checking conda and pip…", PlanningStatus.Describe(checking[..2]));
        Assert.Equal("Checking conda, pip and 2 more…", PlanningStatus.Describe(checking));
    }

    /// <summary>
    /// A provider that fails stops being named, so the line does not go on saying it is being checked
    /// while the pass that failed with it ends.
    /// </summary>
    [Fact]
    public async Task AProviderThatFailsStopsBeingNamed()
    {
        var status = new ProgressRecorder<string>();
        using var failed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var failing = new FakeCleanupProvider("failing")
        {
            WhilePlanning = () => failed.Set(),
            PlanFailure = new InvalidOperationException("The tool would not answer."),
        };

        var waiting = new FakeCleanupProvider("waiting")
        {
            // Holds the pass open until the failure has been reported, so the line is read after it.
            WhilePlanning = () => release.Wait(Deadline),
        };

        var pass = new CleanupPlanner([waiting, failing]).PlanAllAsync(status: status);

        Assert.True(failed.Wait(Deadline));
        SpinWait.SpinUntil(() => status.Reports.Count >= 3, Deadline);
        release.Set();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pass.WaitAsync(Deadline));
        Assert.Equal("Checking waiting…", status.Reports[^1]);
    }

    private static FakeCleanupProvider Sized(string id, long bytes, TimeSpan finishAfter) =>
        new(id)
        {
            Steps = bytes == 0 ? [] : [new DeleteDirectoryStep(@$"C:\Users\testuser\{id}", "Output") { Estimated = new ScanSize(bytes, bytes) }],
            WhilePlanning = () => Thread.Sleep(finishAfter),
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
