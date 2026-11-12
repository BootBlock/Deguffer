using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// The figures a node's labels are worded from: everything <see cref="ExploreNodeText"/> and a
/// row's dates read of it, as one instant sees it. Two equal readings word alike, so a row that
/// keeps the last one can tell that a later tree changed nothing it shows without wording a label.
///
/// <para>That is most rows of most snapshots. While a scan runs only the folders still being walked
/// change, and a folder of files does not change at all, yet every snapshot reaches every row on
/// screen. Wording them all again cost tens of milliseconds and megabytes on the UI thread per
/// snapshot for a folder of twenty thousand entries, all of it to arrive at the labels already
/// there.</para>
///
/// <para>No node number in it. A tree drawn from the file table numbers its nodes afresh in each
/// snapshot, so the same entry under another number has to read as unchanged.</para>
/// </summary>
/// <param name="AgeInDays">
/// The age as <see cref="RelativeAge.DaysSince"/> counts it, rather than the instant it was measured
/// against. The age label is worded from whole days, so a later instant on the same day words it
/// alike, and the first one past the next whole day has to word it again even where nothing in the
/// tree moved.
/// </param>
public readonly record struct ExploreNodeFigures(
    bool IsDirectory,
    long Size,
    long Length,
    FileStorage Storage,
    bool SizeUnknown,
    ExploreTimestamp Created,
    ExploreTimestamp Modified,
    int? AgeInDays)
{
    /// <summary>What <paramref name="node"/> of <paramref name="tree"/> is worded from at <paramref name="now"/>.</summary>
    public static ExploreNodeFigures Of(ExploreTree tree, int node, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var modified = tree.ModifiedOf(node);

        return new ExploreNodeFigures(
            tree.IsDirectory(node),
            tree.SizeOf(node),
            tree.LengthOf(node),
            tree.StorageOf(node),
            tree.HasUnknownSizeBelow(node),
            tree.CreatedOf(node),
            modified,
            RelativeAge.DaysSince(modified.Utc, now));
    }
}
