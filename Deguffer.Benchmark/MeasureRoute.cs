using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Benchmark;

/// <summary>
/// One folder measured as an unelevated scan measures it, through <see cref="ParallelEnumerationScanner"/>
/// with no age guard, as a scan ships, and the walk values given.
///
/// <para><see cref="WalkRoute"/> times the walk under it and nothing a scanner does with each entry.
/// This times the work the scanner adds, its tallies included, so a change to them can be judged
/// against the bare walk of the same folder.</para>
/// </summary>
internal static class MeasureRoute
{
    /// <returns>
    /// The entries a removal would take, and complete where the folder was listed.
    /// </returns>
    public static RunTally Run(string folder, WalkTuning tuning, CancellationToken ct)
    {
        var scanner = new ParallelEnumerationScanner(new ScanTuner(new Chosen(tuning)));

        var result = scanner
            .MeasureAsync(folder, MinimumAge.Off, ct: ct)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return new RunTally(result.Size.Entries, BytesRead: 0, Complete: result.Root is RootReach.Reached);
    }

    /// <summary>
    /// The shipped settings, with <paramref name="tuning"/> chosen for a drive of unknown kind, which
    /// is the kind the tuner answers for every drive.
    /// </summary>
    private sealed class Chosen(WalkTuning tuning) : ICurrentPreferences
    {
        public AppPreferences Current { get; } = AppPreferences.Default with
        {
            Scanning = ScanPreferences.Default.With(
                StorageMedia.Unknown,
                new MediaScanPreferences(WalkThreads: tuning.Threads, ListingBufferKiB: tuning.ListingBufferBytes / 1024)),
        };
    }
}
