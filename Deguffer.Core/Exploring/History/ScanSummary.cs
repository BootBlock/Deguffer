using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.History;

/// <summary>
/// One folder a <see cref="ScanSummary"/> recorded.
/// </summary>
/// <param name="Parent">
/// The position of the folder holding this one in <see cref="ScanSummary.Folders"/>, which is always
/// earlier than this one's, or -1 for the scan's root. Parents first is what lets a reader rebuild a
/// path, or match a folder against a tree, in one pass and without a lookup.
/// </param>
/// <param name="Name">The folder's own name, or the scanned root's path for the root.</param>
/// <param name="Bytes">What the scan counted at or below the folder.</param>
public readonly record struct SummaryFolder(int Parent, string Name, long Bytes);

/// <summary>
/// What one scan of a whole volume found, kept so that a later scan can say what grew (#260).
///
/// <para><b>The largest folders, and every folder above each of them.</b> A folder is never larger
/// than the folder holding it, so taking folders largest first keeps every recorded folder's parent
/// in the record. That property is what makes an absence mean something: a folder that is not
/// recorded, under one that is, held no more than <see cref="UnrecordedAtMost"/> when this was taken.
/// See <see cref="ScanSummaries"/> for how many are taken.</para>
///
/// <para><b>It holds real paths, so it stays on this machine.</b> Nothing writes one to a log, a
/// crash report or a diagnostic, and it is stored under Deguffer's own local data rather than
/// anywhere the user's files are.</para>
/// </summary>
/// <param name="Volume">
/// The <c>\\?\Volume{GUID}\</c> name of the volume scanned. A drive letter can move to another disk,
/// and two scans compared across that move would report one disk's folders as the other's growth.
/// </param>
/// <param name="RootPath">Where the volume was mounted when it was scanned, as the user saw it.</param>
/// <param name="TakenUtc">When the scan finished.</param>
/// <param name="Strategy">
/// Which of §5.5's routes measured it. A walk counts less than the file table does, so a comparison
/// across the two routes is approximate, and says so.
/// </param>
/// <param name="LowerBound">Whether some of the volume could not be read, so every total is a lower bound.</param>
/// <param name="TotalBytes">The volume's capacity when the scan finished.</param>
/// <param name="FreeBytes">What was left of it for this user when the scan finished.</param>
/// <param name="UnrecordedAtMost">
/// The most any folder held that is not in <see cref="Folders"/> but whose parent is. Zero where every
/// folder on the volume was recorded, which makes an absence an exact answer: the folder did not
/// exist.
/// </param>
/// <param name="Folders">The recorded folders, parents first, with the scan's root first of all.</param>
public sealed record ScanSummary(
    string Volume,
    string RootPath,
    DateTime TakenUtc,
    ScanStrategy Strategy,
    bool LowerBound,
    long TotalBytes,
    long FreeBytes,
    long UnrecordedAtMost,
    IReadOnlyList<SummaryFolder> Folders)
{
    /// <summary>What the volume said was in use when the scan finished.</summary>
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>
    /// The path of the folder at <paramref name="index"/>, under <paramref name="rootPath"/> rather
    /// than under the root this summary was taken at, because a volume can be scanned under one drive
    /// letter and compared under another.
    /// </summary>
    public string PathOf(int index, string rootPath)
    {
        var components = new List<string>();

        for (var at = index; at > 0; at = Folders[at].Parent)
        {
            components.Add(Folders[at].Name);
        }

        components.Reverse();

        return Path.Combine([rootPath, .. components]);
    }
}
