using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// One folder measured as an elevated scan measures it the first time it asks about the folder's
/// volume: through <see cref="DirectoryScanner"/> under the shipped route, with a scanner of its own,
/// so each run opens the volume, finds its table, and races the walk against the build of the
/// volume's index. Whichever answers first is the answer, as in a scan.
///
/// <para>The race route starts its walk and its build at the same moment. This times what a scan
/// does before and around them, the open on the caller's thread included, so a change to where the
/// scan reads the table shows here and not there (#544).</para>
///
/// <para>Each run follows an untimed walk of the folder, as a scan's walk follows the walks of the
/// providers before it, and never a build of the folder's own volume, which would slow the walk
/// after it (<see cref="IMftVolume"/>).</para>
/// </summary>
internal static class AutoScanRoute
{
    public static RouteRun On(IMftSourceFactory volumes, string folder, ScanTuner tuner) => RouteRun.Blocking(
        ct => Run(volumes, folder, tuner, ct),
        before: ct => WalkRoute.Run(folder, tuner.ForVolume(folder[0]).Walk, ct));

    /// <returns>The entries the folder holds, complete where the folder was reached.</returns>
    public static RunTally Run(IMftSourceFactory volumes, string folder, ScanTuner tuner, CancellationToken ct)
    {
        var scanner = new DirectoryScanner(volumes, tuning: tuner);

        // Held as a pass holds it, and ended with the run, which stops a build the walk answered before.
        using var pass = scanner.Hold();

        var result = scanner.MeasureAsync(folder, ct: ct).AsTask().GetAwaiter().GetResult();

        return new RunTally(result.Size.Entries, BytesRead: 0, Complete: result.Root is RootReach.Reached);
    }
}
