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
/// The tree as it stood, where one was taken, on either route. Null on most reports: assembling a
/// snapshot is a pass over every node found so far, or every record slot of the file table, so it
/// happens on a slower cadence than the counts. See <see cref="ScanSnapshots"/>.
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
/// <param name="FromIncompleteTable">
/// Whether the tree was drawn from a file table that was not read whole, or that held a record the
/// read could not place, as <see cref="ScannedFolder.FromIncompleteTable"/> says of one folder. Such
/// a record might have been anywhere, so no folder in the tree can be taken to hold all it holds,
/// and nothing that decides a deletion may read the tree in place of the disk.
/// </param>
public sealed record ExploreScan(ExploreTree Tree, ScanStrategy Strategy, FallbackReason Fallback, bool FromIncompleteTable = false)
{
    public static ExploreScan Fast(ExploreTree tree, bool fromIncompleteTable = false) =>
        new(tree, ScanStrategy.MasterFileTable, FallbackReason.None, fromIncompleteTable);

    public static ExploreScan Walked(ExploreTree tree, FallbackReason reason) =>
        new(tree, ScanStrategy.ParallelEnumeration, reason);

    /// <summary>The sentence to show beside the picture, or null when nothing needs saying.</summary>
    public string? RouteNote => ExploreRouteText.Describe(Strategy, Fallback);
}

/// <summary>
/// One folder of <see cref="ExploreScanner.ScanFoldersAsync"/>: the tree its content is in, and the
/// node of that tree that is the folder.
///
/// <para>The tree is shared where the file table answered, because one read of a volume answers for
/// every folder on it, so <paramref name="Node"/> is not always the tree's root, and a caller reads
/// the folder from it rather than from <see cref="ExploreTree.RootNode"/>.</para>
/// </summary>
/// <param name="FromIncompleteTable">
/// Whether the folder was found in a file table that was not read whole, or that held a record the
/// read could not place. Such a record might have been anywhere, this folder included, so what the
/// tree holds for the folder may be short. False for a walked folder, whose tree names each folder
/// it was refused (<see cref="ExploreTree.ListingWasRefused"/>).
/// </param>
public sealed record ScannedFolder(
    ExploreTree Tree, int Node, ScanStrategy Strategy, FallbackReason Fallback, bool FromIncompleteTable)
{
    /// <summary>The sentence to show about the route the folder was read by, or null when nothing needs saying.</summary>
    public string? RouteNote => ExploreRouteText.Describe(Strategy, Fallback);
}

/// <summary>
/// What to tell the user about the route an Explore scan took.
///
/// <para>Separate from <see cref="FallbackReasonText"/>, which says the opposite thing for good
/// reason. That one qualifies the measurement of a dozen named caches, where building the table
/// costs a pass over the whole volume to answer a handful of questions — which is why the walk
/// races the table there, and why its sentence is careful not to promise a speed-up.
/// Here the table answers for every directory on the disk from that same single pass, and for a
/// whole volume the walk it replaces is the one §5.5 measured at over ten minutes. The same fact,
/// and opposite advice. A scan of a folder races the two routes as the clean does, and a walk that
/// wins there says nothing, for the reason its arm below gives.</para>
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
            // Nothing was fallen back from, because there was no other route to take. A folder
            // reached through a junction has no record in the table whose subtree is its content:
            // whatever the link stands for keeps its own place under its real parent, and the walk,
            // which the shell resolves the link for, is simply right. Saying a route was
            // unavailable would be an apology for a choice nobody made.
            FallbackReason.None => null,

            FallbackReason.NotElevated =>
                "Scanned by walking directories. Running Deguffer as administrator lets it read the "
                + "volume's file table instead, which describes every folder on the disk in one pass "
                + "and is much quicker than walking a large one.",
            FallbackReason.NotNtfsVolume =>
                "Scanned by walking directories: this volume is not NTFS, so it has no file table to read.",
            FallbackReason.VolumeNotAddressable =>
                "Scanned by walking directories: this location is not on a local volume Deguffer can open.",
            // Three producers, and one sentence has to be true of all of them: the volume would
            // not open, the table stopped answering part way through, and the table read cleanly
            // and holds no record for this folder. "Did not answer" is what they share, and it is
            // how FallbackReason.MasterFileTableIncomplete defines itself.
            FallbackReason.MasterFileTableIncomplete =>
                "Scanned by walking directories: the volume's file table did not answer for this location.",

            // Belongs to the executor's after-measure and cannot arise here — a scan that draws a
            // picture never asks for a reading taken across a change to the disk.
            FallbackReason.FreshReadingRequired => null,

            FallbackReason.WalkChosen =>
                "Scanned by walking directories, because Settings asks for the walk only.",

            // A scan of a folder that walked while the table was read, and finished first. Nothing
            // was lost and nothing is on offer: the table opened, so the process already has the
            // rights, and the walk listed the folder itself, so its tree is as whole as the table's
            // and names any folder below it that it was refused.
            FallbackReason.WalkAnsweredFirst => null,

            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
    }
}
