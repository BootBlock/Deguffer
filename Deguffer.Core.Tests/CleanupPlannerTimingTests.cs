using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Timing;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A planning pass says when each provider took a slot and ended (#443), on the clock the planner was
/// given, and says nothing of a provider that did not plan.
/// </summary>
public sealed class CleanupPlannerTimingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A provider whose plan waits three seconds on the clock is reported as taking three seconds, from
    /// an offset the clock had reached when it took its slot.
    /// </summary>
    [Fact]
    public async Task AProviderIsReportedWithTheTimeItsPlanTook()
    {
        var clock = new ManualTimeProvider();
        var slow = new FakeCleanupProvider("slow") { WhilePlanning = () => clock.Advance(TimeSpan.FromSeconds(3)) };
        var timed = new PlanTimeLog();

        await new CleanupPlanner([slow], time: clock).PlanAllAsync(timed: timed).WaitAsync(Deadline);

        var time = Assert.Single(timed.Times);
        Assert.Same(slow, time.Provider);
        Assert.Equal(TimeSpan.Zero, time.Began);
        Assert.Equal(TimeSpan.FromSeconds(3), time.Ended);
        Assert.Equal(TimeSpan.FromSeconds(3), time.Elapsed);
    }

    /// <summary>
    /// Offsets are from the start of each pass, not from when the planner was built or from the pass
    /// before, so two passes of one planner, as the Storage page runs them, read alike.
    /// </summary>
    [Fact]
    public async Task OffsetsAreFromTheStartOfEachPass()
    {
        var clock = new ManualTimeProvider();
        var provider = new FakeCleanupProvider("timed") { WhilePlanning = () => clock.Advance(TimeSpan.FromSeconds(2)) };
        var planner = new CleanupPlanner([provider], time: clock);
        var expected = new ProviderPlanTime(provider, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromSeconds(7));
        var firstPass = new PlanTimeLog();
        await planner.PlanAllAsync(timed: firstPass).WaitAsync(Deadline);

        clock.Advance(TimeSpan.FromSeconds(1));
        var secondPass = new PlanTimeLog();
        await planner.PlanAllAsync(timed: secondPass).WaitAsync(Deadline);

        Assert.Equal([expected], firstPass.Times);
        Assert.Equal([expected], secondPass.Times);
    }

    /// <summary>
    /// A provider that waits for a slot starts late, and says so: with one more provider than the pass
    /// has slots, the last cannot start until a provider has ended, and each that ends has moved the
    /// clock on a second.
    /// </summary>
    [Fact]
    public async Task AProviderThatWaitsForASlotStartsLate()
    {
        var clock = new ManualTimeProvider();
        var providers = Enumerable.Range(0, CleanupPlanner.ProvidersAtOnce + 1)
            .Select(index => new FakeCleanupProvider($"p{index}") { WhilePlanning = () => clock.Advance(TimeSpan.FromSeconds(1)) })
            .ToList();
        var timed = new PlanTimeLog();

        await new CleanupPlanner(providers, time: clock).PlanAllAsync(timed: timed).WaitAsync(Deadline);

        Assert.Equal(providers.Count, timed.Times.Count);
        Assert.True(timed.Times.Max(time => time.Began) >= TimeSpan.FromSeconds(1), "No provider started after another ended.");
        Assert.All(timed.Times, time => Assert.True(time.Ended >= time.Began));
    }

    /// <summary>A provider whose plan failed did not plan, so nothing is said of its time.</summary>
    [Fact]
    public async Task AProviderThatFailedIsNotTimed()
    {
        var failing = new FakeCleanupProvider("failing") { PlanFailure = new InvalidOperationException("Broken.") };
        var timed = new PlanTimeLog();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CleanupPlanner([failing], time: new ManualTimeProvider()).PlanAllAsync(timed: timed).WaitAsync(Deadline));

        Assert.Empty(timed.Times);
    }

    /// <summary>
    /// Every time is reported before its finding, so a caller that keeps both has the time of every row
    /// it has been handed.
    /// </summary>
    [Fact]
    public async Task EachTimeIsReportedBeforeItsFinding()
    {
        var providers = Enumerable.Range(0, 12).Select(index => new FakeCleanupProvider($"p{index}")).ToList();
        var timed = new PlanTimeLog();
        var missing = 0;
        var found = new Recorder<Finding>(finding =>
        {
            if (!timed.Times.Any(time => ReferenceEquals(time.Provider, finding.Provider)))
            {
                Interlocked.Increment(ref missing);
            }
        });

        await new CleanupPlanner(providers).PlanAllAsync(found: found, timed: timed).WaitAsync(Deadline);

        Assert.Equal(0, missing);
        Assert.Equal(providers.Count, timed.Times.Count);
    }

    private sealed class Recorder<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
