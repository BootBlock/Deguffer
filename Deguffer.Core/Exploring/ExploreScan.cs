using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// How far a scan has got. Reported while it runs, so §5.5's "never block on a complete scan" holds
/// for a whole volume as it does for one cache.
/// </summary>
/// <param name="Done">
/// Records read, or entries found, depending on the route. Only ever compared against
/// <paramref name="Total"/>.
/// </param>
/// <param name="Total">
/// What <paramref name="Done"/> is counting towards, or null where nothing knows. The file table
/// states its own record count up front, so that route drives a real progress bar; a walk cannot
/// know how many directories it has yet to open, so that one is honest about being indeterminate
/// rather than inventing a denominator.
/// </param>
/// <param name="BytesSeen">Bytes accounted for so far.</param>
/// <param name="Snapshot">
/// The tree as it stood, where one was taken. Null on most reports: assembling a snapshot copies
/// every array, so it happens on a slow cadence rather than per level.
/// </param>
public sealed record ExploreProgress(long Done, long? Total, long BytesSeen, ExploreTree? Snapshot = null)
{
    /// <summary>How far through, or null where the route cannot say.</summary>
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Done / Total.Value, 0, 1) : null;
}

/// <summary>
/// A finished scan, and how it was obtained. §5.5 requires the fallback to be observable, so the
/// route is part of the result rather than something a caller infers from the elapsed time.
/// </summary>
public sealed record ExploreScan(ExploreTree Tree, ScanStrategy Strategy, FallbackReason Fallback)
{
    public static ExploreScan Fast(ExploreTree tree) =>
        new(tree, ScanStrategy.MasterFileTable, FallbackReason.None);

    public static ExploreScan Walked(ExploreTree tree, FallbackReason reason) =>
        new(tree, ScanStrategy.ParallelEnumeration, reason);

    /// <summary>The sentence to show beside the picture, or null when nothing needs saying.</summary>
    public string? RouteNote => ExploreRouteText.Describe(Strategy, Fallback);
}

/// <summary>
/// What to tell the user about the route a whole-volume scan took.
///
/// <para>Separate from <see cref="FallbackReasonText"/>, which says the opposite thing for good
/// reason. That one qualifies the measurement of a dozen named caches, where building the table
/// costs a pass over the whole volume to answer a handful of questions — measured at 9.9 seconds
/// against 1.24 for walking the same paths — so its sentence is careful not to promise a speed-up.
/// Here the table answers for every directory on the disk from that same single pass, and the walk
/// it replaces is the one §5.5 measured at over ten minutes. The same fact, and opposite
/// advice.</para>
/// </summary>
public static class ExploreRouteText
{
    public static string? Describe(ScanStrategy strategy, FallbackReason reason)
    {
        if (strategy == ScanStrategy.MasterFileTable)
        {
            return null;
        }

        return reason switch
        {
            // Nothing was fallen back from: the table is read from a volume's root, and this scan
            // started somewhere below one. Saying a route was unavailable would be an apology for a
            // choice nobody made.
            FallbackReason.None => null,

            FallbackReason.NotElevated =>
                "Scanned by walking directories. Running Deguffer as administrator lets it read the "
                + "volume's file table instead, which describes the whole disk in one pass and is "
                + "much quicker on a full drive.",
            FallbackReason.NotNtfsVolume =>
                "Scanned by walking directories: this volume is not NTFS, so it has no file table to read.",
            FallbackReason.VolumeNotAddressable =>
                "Scanned by walking directories: this location is not on a local volume Deguffer can open.",
            FallbackReason.MasterFileTableIncomplete =>
                "Scanned by walking directories: the volume's file table could not be read.",

            // Belongs to the executor's after-measure and cannot arise here — a scan that draws a
            // picture never asks for a reading taken across a change to the disk.
            FallbackReason.FreshReadingRequired => null,

            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
    }
}
