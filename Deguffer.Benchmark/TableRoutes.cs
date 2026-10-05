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
/// </summary>
internal static class TableRoutes
{
    /// <summary>
    /// Open the volume, or say why it cannot be. Asked once before any run is timed, so a run
    /// without administrator rights stops with the reason rather than with a timing of nothing.
    /// </summary>
    public static FallbackReason Probe(char drive)
    {
        using var source = VolumeMftSource.TryOpen(drive, out var reason);
        return reason;
    }

    public static RunTally Run(Route route, char drive, CancellationToken ct)
    {
        // Opened above moments before, so a failure here is the volume going away mid-benchmark.
        using var source = new CountingMftSource(
            VolumeMftSource.TryOpen(drive, out var reason)
            ?? throw new IOException($"The volume could not be opened again: {reason}."));

        var count = (int)Math.Min(source.RecordCount, int.MaxValue);

        var complete = route switch
        {
            Route.Table => ReadOnly(source, count, ct),
            Route.Index => MftVolumeIndexBuilder.TryBuild(source, out _, ct),
            Route.Explore => MftExploreReader.Read(source, $"{drive}:\\", [], onProgress: null, ct).Tree is not null,
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Not a route that reads the table."),
        };

        return new RunTally(source.RecordsRead, source.BytesRead, complete && source.RecordsRead >= count);
    }

    /// <summary>
    /// Read and parse every record and keep none of them: the floor the two structures are built on.
    /// </summary>
    private static bool ReadOnly(IMftSource source, int count, CancellationToken ct) =>
        MftRecordStream.TryReadAll(source, count, static (_, _, in _) => true, ct);
}
