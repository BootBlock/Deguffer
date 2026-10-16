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
        var fullBefore = GC.CollectionCount(2);
        var clock = Stopwatch.StartNew();

        var tally = run(ct);

        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var full = GC.CollectionCount(2) - fullBefore;

        using var self = Process.GetCurrentProcess();
        var peak = self.PeakWorkingSet64;

        // After the clock, and after the counts above, because measuring it is a full collection of
        // its own. The result is let go here, so a run does not hold the last run's tree.
        var keptBytes = 0L;
        if (tally.Kept is { } kept)
        {
            // The heap with the result and then without it, a moment apart, rather than against the
            // heap before the run: a run leaves buffers in shared pools for the next to rent, and
            // those are not what the result keeps.
            var holding = GC.GetTotalMemory(forceFullCollection: true);
            kept.Release();
            keptBytes = holding - GC.GetTotalMemory(forceFullCollection: true);
            tally = tally with { Kept = null };
        }

        return new RunSample(clock.Elapsed, tally, allocated, full, peak, keptBytes);
    }
}
