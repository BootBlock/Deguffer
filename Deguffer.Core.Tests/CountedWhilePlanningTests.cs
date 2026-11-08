using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.5 asks a preview to stream partial results rather than block on a complete scan. While a provider
/// walks a large location, the status line names it with the bytes its measurements have counted so
/// far, and stops the moment its finding arrives.
/// </summary>
public sealed class CountedWhilePlanningTests
{
    /// <summary>Far longer than a walk of four small folders takes, and far shorter than a hung test.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One provider measures three folders in one walk, and the walk is held part-way through, on a
    /// folder below the third. The line shows the sum of all three before the walk ends, then nothing
    /// once the finding is reported.
    ///
    /// <para>Every report moves the walk's clock on by its interval, so the next folder read is due to
    /// report too, and whichever of the three is read last reports a sum that holds all of them.</para>
    /// </summary>
    [Fact]
    public async Task AProviderMeasuringThreePathsShowsTheirSumBeforeItsWalkEnds()
    {
        using var temp = new TempDirectory();
        var a = Path.GetDirectoryName(temp.CreateFile(1024, "a", "one.bin"))!;
        var b = Path.GetDirectoryName(temp.CreateFile(2048, "b", "two.bin"))!;
        var c = Path.GetDirectoryName(temp.CreateFile(4096, "c", "three.bin"))!;
        var held = Path.GetDirectoryName(temp.CreateFile(8192, "c", "held", "four.bin"))!;

        using var release = new ManualResetEventSlim();
        var clock = new ManualTimeProvider();

        ListDirectory holding = (directory, request, into) =>
        {
            if (LongPath.Display(directory).Equals(held, StringComparison.OrdinalIgnoreCase))
            {
                release.Wait(Deadline);
            }

            return DirectoryQuery.List(directory, request, into);
        };

        var lines = new List<string>();
        var status = new CallbackProgress<string>(line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }

            clock.Advance(BoundedFileWalk.ProgressInterval);
        });

        var linesWhenFound = -1;
        var found = new CallbackProgress<Finding>(_ =>
        {
            lock (lines)
            {
                linesWhenFound = lines.Count;
            }
        });

        var provider = new TargetsProvider(
            new FakeUserEnvironment(temp.CreateDirectory("profile")),
            new ParallelEnumerationScanner(ScanTuner.Shipped, holding, clock),
            [.. new[] { a, b, c }.Select(path => new DeletionTarget(path, "Measured"))]);

        var pass = new CleanupPlanner([provider]).PlanAllAsync(MinimumAge.Off, status, found);

        var allThree = $"Checking Targets ({FreeSpace.Format(1024 + 2048 + 4096)} counted so far)…";
        var shownWhileHeld = SpinWait.SpinUntil(
            () =>
            {
                lock (lines)
                {
                    return lines.Contains(allThree);
                }
            },
            Deadline);

        release.Set();
        var findings = await pass.WaitAsync(Deadline);

        Assert.True(shownWhileHeld, $"The line never showed all three folders while the walk was held: {string.Join(" | ", lines)}");
        Assert.Equal(1024 + 2048 + 4096 + 8192, Assert.Single(findings).EstimatedBytes);

        lock (lines)
        {
            Assert.Equal(linesWhenFound, lines.Count);
            Assert.Equal("Checking Targets…", lines[0]);
            Assert.All(lines.Skip(1), line => Assert.Contains("counted so far", line, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A provider that measures twice, one measurement after the other or both at once, shows the sum of
    /// both. Each measurement reports from zero, so a figure taken from the latest alone would fall back
    /// to nothing as the second began.
    /// </summary>
    [Fact]
    public void AProvidersSecondMeasurementAddsToItsFirst()
    {
        var counted = new ProgressRecorder<ScanSize>();
        var counting = new CountedSoFar(counted);

        var first = counting.Measurement();
        first.Report(ScanSize.FromLengths(5000));

        var second = counting.Measurement();
        second.Report(ScanSize.FromLengths(100));
        first.Report(ScanSize.FromLengths(6000));

        Assert.Equal([5000L, 5100L, 6100L], counted.Reports.Select(size => size.Reclaimable));
    }

    /// <summary>
    /// A measurement the provider did not wait for reports nothing once its plan is over, so the line
    /// cannot move for a provider whose finding is already on screen.
    /// </summary>
    [Fact]
    public void NothingIsReportedOnceThePlanIsOver()
    {
        var counted = new ProgressRecorder<ScanSize>();
        var counting = new CountedSoFar(counted);
        var measurement = counting.Measurement();

        measurement.Report(ScanSize.FromLengths(5000));
        counting.End();
        measurement.Report(ScanSize.FromLengths(9000));

        Assert.Equal(5000, Assert.Single(counted.Reports).Reclaimable);
    }

    /// <summary>
    /// The figure reaches the planner through the provider's own measurements: a provider planned with
    /// a sink reports through it, and the plan's figure is still the finished measurement.
    /// </summary>
    [Fact]
    public async Task AProvidersPlanReportsWhatItsMeasurementsCount()
    {
        using var temp = new TempDirectory();
        var a = Path.GetDirectoryName(temp.CreateFile(1000, "a", "one.bin"))!;
        var b = Path.GetDirectoryName(temp.CreateFile(3000, "b", "two.bin"))!;
        var counted = new ProgressRecorder<ScanSize>();

        var plan = await new TargetsProvider(
            new FakeUserEnvironment(temp.CreateDirectory("profile")),
            new FakeDirectoryScanner(),
            new DeletionTarget(a, "Measured"),
            new DeletionTarget(b, "Measured")).PlanAsync(MinimumAge.Off, counted);

        Assert.Equal(4000, plan.EstimatedBytes);
        Assert.Equal(4000, counted.Reports[^1].Reclaimable);
    }
}
