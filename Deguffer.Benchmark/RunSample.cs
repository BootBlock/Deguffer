namespace Deguffer.Benchmark;

/// <summary>One timed run.</summary>
/// <param name="AllocatedBytes">Bytes allocated on every thread while the run was timed.</param>
/// <param name="FullCollections">
/// How many full (generation 2) collections the run started. Each traces everything still alive, which
/// for a route that builds a tree is the tree so far.
/// </param>
/// <param name="PeakWorkingSet">
/// The process's peak working set when the run ended. Windows keeps one peak for the life of the
/// process, so after the first run this is the highest any run has reached rather than this run's
/// own.
/// </param>
/// <param name="KeptBytes">
/// The managed heap the route's result holds: the heap after a full collection with the result
/// still referenced, less the heap after one without it. What a scan keeps for as long as it is shown,
/// which the peak working set cannot say: that counts what the run threw away too. Zero for a route
/// that keeps nothing.
/// </param>
internal readonly record struct RunSample(
    TimeSpan Elapsed, RunTally Tally, long AllocatedBytes, int FullCollections, long PeakWorkingSet, long KeptBytes = 0)
{
    public double ItemsPerSecond => Tally.Items / Seconds;

    public double MegabytesPerSecond => Tally.BytesRead / (1024.0 * 1024.0) / Seconds;

    /// <summary>Never zero, so a run too short for the clock to see gives a large rate, not infinity.</summary>
    private double Seconds => Math.Max(Elapsed.TotalSeconds, 1e-9);
}
