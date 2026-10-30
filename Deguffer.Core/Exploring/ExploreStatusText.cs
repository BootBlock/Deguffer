using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What the Explore page's status line says of a finished scan and of where the page is pointed,
/// and when a refusal it stated is taken back.
/// </summary>
public static class ExploreStatusText
{
    /// <summary>
    /// What the line says before anything has been measured, and what it goes back to when a refusal
    /// is taken off it. See <see cref="Retargeted"/>.
    /// </summary>
    public const string Prompt = "Choose a drive or a folder and scan it to see what is using the space.";

    /// <summary>
    /// What a finished scan says: which figure the map draws, once, where the total is, with the
    /// length beside it so a gap between the two is something the reader was told about rather than
    /// found; that the totals are lower bounds where any of it could not be read (§7.1); what the
    /// route's figures leave out; and where they count more than the drive has in use.
    /// </summary>
    /// <param name="volume">What the volume held when the scan finished. See <see cref="VolumeSpace"/>.</param>
    /// <param name="scoped">
    /// Whether the scan was started on a folder rather than a drive, read as it started: the scope can
    /// change while a scan runs, and the sentence describes this scan rather than the next one.
    /// </param>
    public static string Finished(ExploreScan scan, VolumeSpace volume, bool scoped)
    {
        ArgumentNullException.ThrowIfNull(scan);

        return string.Join(" ", new[]
        {
            $"{FreeSpace.Format(scan.Tree.TotalBytes)} on disk accounted for, in files whose lengths "
                + $"add up to {FreeSpace.Format(scan.Tree.TotalLength)}.",
            scan.Tree.HasUnknownSizes
                ? $"Some of this {(scoped ? "folder" : "drive")} could not be read, so the totals are lower bounds."
                : null,
            ExploreRouteText.Sizing(scan.Strategy),
            ExploreUnaccountedNote.Overcount(volume, scan.Tree.TotalBytes, scan.Strategy),
        }.Where(sentence => sentence is not null));
    }

    /// <summary>
    /// What the line says when the folder picker returned something that is not a folder on a disk.
    /// The target stays where it was, so a refusal of it still stands and is said again: without it
    /// the Scan button stays greyed out with nothing on the page saying why.
    /// </summary>
    /// <param name="standing">Why the page refuses the target it keeps, or null where it does not.</param>
    public static string NotOnDisk(string? standing) =>
        standing is null ? PickedFolder.NotOnDisk : $"{PickedFolder.NotOnDisk} {standing}";

    /// <summary>
    /// What the line says once the page is pointed somewhere new: why the page will not scan it, or
    /// the prompt where the line still holds a refusal the page stated and nothing refuses now, or
    /// whatever it said before.
    ///
    /// <para>§7.1 asks a refusal to say what it is, and a greyed-out Scan button says nothing at all.
    /// The retraction is the other half of that: the line describes what pressing Scan would do, so
    /// leaving "Deguffer does not scan this drive" up while the button is live and aimed at an
    /// ordinary volume states the opposite of the truth.</para>
    ///
    /// <para>It takes back its own sentence and nobody else's. A scan's result and a selection's
    /// report write this same line, and neither is made untrue by a change of target, so clearing
    /// unconditionally would throw away whichever of them was there.</para>
    /// </summary>
    /// <param name="shown">What the line says now.</param>
    /// <param name="stated">The refusal the page last put on the line, or null where it put none since.</param>
    /// <param name="refusal">Why the page will not scan where it now points, or null where it will.</param>
    public static string Retargeted(string shown, string? stated, string? refusal) =>
        refusal ?? (stated is not null && shown == stated ? Prompt : shown);
}
