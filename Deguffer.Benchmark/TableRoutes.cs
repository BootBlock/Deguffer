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
    /// <param name="target">
    /// The volume's root, or for <see cref="Route.ExploreFolder"/> the folder, fully qualified.
    /// </param>
    public static RunTally Run(Route route, IMftSourceFactory volumes, string target, TableTuning tuning, CancellationToken ct)
    {
        if (!VolumePath.TryParse(target, out var place))
        {
            throw new ArgumentException("A table route reads a volume, so its target has a drive letter.", nameof(target));
        }

        // Opened above moments before, so a failure here is the volume going away mid-benchmark.
        using var source = new CountingMftSource(
            volumes.TryOpen(place.DriveLetter, out var reason)
            ?? throw new IOException($"The volume could not be opened again: {reason}."));

        var count = (int)Math.Min(source.RecordCount, int.MaxValue);

        return route switch
        {
            Route.Table => Tally(source, ReadOnly(source, count, tuning, ct)),
            Route.Index => Tally(source, MftVolumeIndexBuilder.TryBuild(source, tuning, out _, ct)),
            Route.Explore => Explore(source, $"{place.DriveLetter}:\\", [], tuning, ct),
            Route.ExploreFolder => Explore(source, place.FullPath, place.Components, tuning, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Not a route that reads the table."),
        };
    }

    /// <summary>
    /// The tree for <paramref name="rootPath"/>, complete where the table was read whole and the
    /// folder was found in it, and handed back for what it keeps to be measured.
    /// </summary>
    private static RunTally Explore(
        CountingMftSource source, string rootPath, IReadOnlyList<string> components, TableTuning tuning, CancellationToken ct)
    {
        var read = MftExploreReader.Read(source, rootPath, components, tuning, onProgress: null, ct);

        return Tally(source, read is { Tree: not null, WholeTable: true }) with { Kept = read.Tree };
    }

    private static RunTally Tally(CountingMftSource source, bool complete) =>
        new(source.RecordsRead, source.BytesRead, complete);

    /// <summary>
    /// Read and parse every record and keep none of them: the floor the two structures are built on.
    /// </summary>
    private static bool ReadOnly(IMftSource source, int count, TableTuning tuning, CancellationToken ct) =>
        MftRecordStream.TryReadAll(source, count, tuning, static (_, _, in _, _) => true, onProgress: null, ct);
}
