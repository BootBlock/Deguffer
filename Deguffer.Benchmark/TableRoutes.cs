using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// The three routes that read a volume's file table, each run exactly as a scan runs it.
///
/// <para>Each run opens the volume again. A scan opens it once per scan, so opening it here once
/// for every run would leave the open and the boot-sector read out of every run after the first.
/// </para>
///
/// <para>Opened through <see cref="IMftSourceFactory"/>, the scanner's own seam, so whether a run
/// counts as complete is tested against a fixture table rather than this machine's volumes.</para>
/// </summary>
internal static class TableRoutes
{
    /// <summary>
    /// Open the volume, or say why it cannot be. Asked once before any run is timed, so a run
    /// without administrator rights stops with the reason rather than with a timing of nothing.
    /// </summary>
    public static FallbackReason Probe(IMftSourceFactory volumes, char drive)
    {
        using var source = volumes.TryOpen(drive, out var reason);
        return reason;
    }

    /// <summary>
    /// Run <paramref name="route"/> once. Complete only where the route read every record in use and,
    /// for the index, did not abandon the volume. Records <c>$MFT</c>'s <c>$BITMAP</c> marks free are
    /// never read, so how many records were read cannot say whether the table was read whole.
    /// </summary>
    public static RunTally Run(Route route, IMftSourceFactory volumes, char drive, TableTuning tuning, CancellationToken ct)
    {
        // Opened above moments before, so a failure here is the volume going away mid-benchmark.
        using var source = new CountingMftSource(
            volumes.TryOpen(drive, out var reason)
            ?? throw new IOException($"The volume could not be opened again: {reason}."));

        var count = (int)Math.Min(source.RecordCount, int.MaxValue);

        var complete = route switch
        {
            Route.Table => ReadOnly(source, count, tuning, ct),
            Route.Index => MftVolumeIndexBuilder.TryBuild(source, tuning, out _, ct),
            Route.Explore => MftExploreReader.Read(source, $"{drive}:\\", [], tuning, onProgress: null, ct) is { Tree: not null, WholeTable: true },
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Not a route that reads the table."),
        };

        return new RunTally(source.RecordsRead, source.BytesRead, complete);
    }

    /// <summary>
    /// Read and parse every record and keep none of them: the floor the two structures are built on.
    /// </summary>
    private static bool ReadOnly(IMftSource source, int count, TableTuning tuning, CancellationToken ct) =>
        MftRecordStream.TryReadAll(source, count, tuning, static (_, _, in _) => true, onProgress: null, ct);
}
