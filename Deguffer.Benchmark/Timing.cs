using System.Diagnostics;

namespace Deguffer.Benchmark;

/// <summary>
/// Runs one route once and measures it.
/// </summary>
internal static class Timing
{
    public static RunSample Measure(Func<CancellationToken, RunTally> run, CancellationToken ct)
    {
        // What the previous run left behind is collected before the clock starts, so one run's
        // garbage is not collected inside the next run's time.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Precise, and process-wide: both routes allocate on several threads, and the imprecise
        // figure leaves out what each thread has not yet handed back.
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();

        var tally = run(ct);

        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        using var self = Process.GetCurrentProcess();

        return new RunSample(clock.Elapsed, tally, allocated, self.PeakWorkingSet64);
    }
}
