using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark;

/// <summary>
/// One tree removed through <see cref="DirectoryRemover"/>, as a clean removes a cache or a build
/// folder: the walk, the file pass and the folder pass, each timed by the removal itself
/// (<see cref="RemovalOutcome.Passes"/>).
///
/// <para>The tree is built again before every run, in a folder of its own inside the
/// <see cref="RemovalSite"/>, beside the site's kept folder.</para>
/// </summary>
internal sealed class RemoveRoute(RemovalSite site, TreeShape shape)
{
    private string Tree => site.Place("tree");

    public RouteRun Timed => RouteRun.Blocking(Run, Build, site.ProveBesideStanding);

    private void Build(CancellationToken ct) => SynthesisedTree.Build(Tree, shape, ct);

    private RunTally Run(CancellationToken ct)
    {
        var workers = WorkerThreadPeak.Watch(WorkerThreads.Shared);
        RemovalOutcome removal;
        int peakWorkers;

        try
        {
            removal = DirectoryRemover.RemoveAsync(Tree, ct: ct).GetAwaiter().GetResult();
        }
        finally
        {
            peakWorkers = workers.Stop();
        }

        // The removal returns what it did when it is cancelled rather than throwing. A run stopped
        // part-way is not a result, so it is thrown here for the run loop to report.
        ct.ThrowIfCancellationRequested();

        return new RunTally(
            removal.EntriesRemoved,
            BytesRead: 0,
            Complete: removal.RootRemoved && removal.Refused.IsEmpty && removal.LeftStanding.Count == 0,
            PeakWorkers: peakWorkers,
            Passes: removal.Passes);
    }
}
