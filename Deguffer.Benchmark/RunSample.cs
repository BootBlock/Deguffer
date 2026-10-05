namespace Deguffer.Benchmark;

/// <summary>One timed run.</summary>
/// <param name="AllocatedBytes">Bytes allocated on every thread while the run was timed.</param>
/// <param name="PeakWorkingSet">
/// The process's peak working set when the run ended. Windows keeps one peak for the life of the
/// process, so after the first run this is the highest any run has reached rather than this run's
/// own.
/// </param>
internal readonly record struct RunSample(TimeSpan Elapsed, RunTally Tally, long AllocatedBytes, long PeakWorkingSet)
{
    public double ItemsPerSecond => Tally.Items / Seconds;

    public double MegabytesPerSecond => Tally.BytesRead / (1024.0 * 1024.0) / Seconds;

    /// <summary>Never zero, so a run too short for the clock to see gives a large rate, not infinity.</summary>
    private double Seconds => Math.Max(Elapsed.TotalSeconds, 1e-9);
}
