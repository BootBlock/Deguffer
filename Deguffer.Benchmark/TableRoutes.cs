using System.Diagnostics;
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
    /// <param name="census">
    /// How many records are in use, or null where the volume cannot be opened or its bitmap read.
    /// </param>
    public static FallbackReason Probe(IMftSourceFactory volumes, char drive, out TableCensus? census)
    {
        using var source = volumes.TryOpenTable(drive, out var reason);

        census = null;

        if (source is not null)
        {
            var count = Count(source);

            if (MftBitmapReader.TryRead(source, count) is { } bitmap)
            {
                census = new TableCensus(bitmap.CountInUse(count), source.BytesPerRecord);
            }
        }

        return reason;
    }

    /// <summary>
    /// Run <paramref name="route"/> once. Complete only where the route read every record in use and,
    /// for the index, did not abandon the volume. Records <c>$MFT</c>'s <c>$BITMAP</c> marks free are
    /// never read, so how many records were read cannot say whether the table was read whole.
    /// </summary>
    /// <param name="target">
    /// The volume's root, or for <see cref="Route.ExploreFolder"/> the folder, fully qualified.
    /// </param>
    /// <param name="opened">
    /// Called with the volume once it is open and before its table is read, as a scan's build hands
    /// its volume to the priorities it reads at, or null for nothing.
    /// </param>
    public static RunTally Run(
        Route route, IMftSourceFactory volumes, string target, TableTuning tuning, Action<IMftVolume>? opened, CancellationToken ct)
    {
        if (!VolumePath.TryParse(target, out var place))
        {
            throw new ArgumentException("A table route reads a volume, so its target has a drive letter.", nameof(target));
        }

        // Timed apart from the stages, which the read times, because opening reads the boot sector
        // and $MFT's own extent map, and a system volume's $MFT can be in many pieces. The open times
        // its own steps as well, so a slow one can be traced to the handle or to the reads: finding
        // the table pays NTFS's flush of the volume (IMftVolume). Both steps run here at once, as an
        // Explore scan runs them.
        var open = new MftOpenTimings();
        var opening = Stopwatch.GetTimestamp();

        // Opened above moments before, so a failure here is the volume going away mid-benchmark.
        using var volume = volumes.TryOpen(place.DriveLetter, out var reason, open)
            ?? throw new IOException($"The volume could not be opened again: {reason}.");

        opened?.Invoke(volume);

        using var source = new CountingMftSource(
            volume.TryReadTable(out reason)
            ?? throw new IOException($"The table could not be found again: {reason}."));

        var openTime = Stopwatch.GetElapsedTime(opening);
        var timings = new MftStageTimings();

        var tally = route switch
        {
            Route.Table => Tally(source, ReadOnly(source, Count(source), tuning, timings, ct), timings),
            Route.Index => Tally(source, MftVolumeIndexBuilder.TryBuild(source, tuning, out _, ct, timings), timings),
            Route.Explore => Explore(source, $"{place.DriveLetter}:\\", [], tuning, timings, ct),
            Route.ExploreFolder => Explore(source, place.FullPath, place.Components, tuning, timings, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Not a route that reads the table."),
        };

        return tally with { OpenTime = openTime, Open = open };
    }

    /// <summary>
    /// The tree for <paramref name="rootPath"/>, complete where the table was read whole and the
    /// folder was found in it, and handed back for what it keeps to be measured.
    /// </summary>
    private static RunTally Explore(
        CountingMftSource source,
        string rootPath,
        IReadOnlyList<string> components,
        TableTuning tuning,
        MftStageTimings timings,
        CancellationToken ct)
    {
        var read = MftExploreReader.Read(source, rootPath, components, tuning, progress: null, ct, timings: timings);

        return Tally(source, read is { Tree: not null, WholeTable: true }, timings) with
        {
            Kept = read.Tree is { } tree ? new KeptResult(tree) : null,
        };
    }

    private static RunTally Tally(CountingMftSource source, bool complete, MftStageTimings timings) =>
        new(
            source.RecordsRead,
            source.BytesRead,
            complete,
            Reads: source.Reads,
            ClusterReads: source.ClusterReads,
            ListReadTime: source.ListReadTime,
            MeanReadBytes: source.MeanReadBytes,
            Stages: timings);

    /// <summary>The records a pass reads: every record the table holds, as far as a pass can number them.</summary>
    private static int Count(IMftSource source) => (int)Math.Min(source.RecordCount, int.MaxValue);

    /// <summary>
    /// Read and parse every record and keep none of them: the floor the two structures are built on.
    /// </summary>
    private static bool ReadOnly(IMftSource source, int count, TableTuning tuning, MftStageTimings timings, CancellationToken ct) =>
        MftRecordStream.TryReadAll(source, count, tuning, static (_, _, in _, _, _) => true, onProgress: null, ct, timings);
}
