using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The walk a route chooser (<see cref="VolumeRoutes"/>) sends a path to where the volume's table
/// does not answer it: <see cref="ParallelEnumerationScanner"/> for a figure of every name, and
/// <see cref="SoleLinkWalk"/> for a figure of the files nothing else links.
/// </summary>
internal interface IWalkRoute
{
    /// <summary>The same walk, attributed to whichever reason sent the caller to it.</summary>
    IWalkRoute Because(FallbackReason reason);

    /// <summary>
    /// Each of <paramref name="asked"/> measured once for each of <paramref name="keeps"/>, in their
    /// orders, from one reading of it, so the walk is asked the question the table is asked. Each
    /// path's first reading goes to <paramref name="running"/> as it rises.
    /// </summary>
    Task<ScanResult[][]> MeasureEachAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge[] keeps,
        RunningPaths running,
        CancellationToken ct);
}
