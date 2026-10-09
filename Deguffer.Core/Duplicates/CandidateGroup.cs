using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// A file a search found that may have a match: one file, however many paths reached it and however
/// many names it has, identified as Windows knows it (§7.4).
/// </summary>
/// <param name="Identity">The file's volume and number, which is what makes two paths one file.</param>
/// <param name="Path">Where Windows says the file is, in display form.</param>
/// <param name="Name">The name the search found it under, which is what a name match compares.</param>
/// <param name="Names">
/// Every name the file has, in display form: each of its hard links where it has several, and
/// <paramref name="Path"/> alone where it has one. Where Windows would not list a linked file's
/// names, the paths the search reached it by.
/// </param>
/// <param name="NameCount">
/// How many names Windows says the file has. More than one makes it a file no removal of a name can
/// free, which is never marked and never counted.
/// </param>
/// <param name="Length">Its length in bytes when it was identified, which is what a size match compares.</param>
/// <param name="Modified">Its last-modified time to the file system's full precision, which is what a time match compares.</param>
/// <param name="Storage">
/// How it is stored: whether it is in the cloud as Windows said when it was identified, and
/// otherwise as the scan saw it. A cloud file that was online-only at either time is never in a
/// content search, and the check before any read asks again.
/// </param>
/// <param name="Role">
/// The role of the innermost location holding it, and a reference where any path reaching it is in
/// a reference location.
/// </param>
public sealed record DuplicateCandidate(
    FileIdentity Identity,
    string Path,
    string Name,
    IReadOnlyList<string> Names,
    int NameCount,
    long Length,
    DateTime Modified,
    FileStorage Storage,
    LocationRole Role)
{
    /// <summary>Whether the file has hard links, so removing one name would free nothing.</summary>
    public bool HasSeveralNames => NameCount > 1;
}

/// <summary>
/// Files that share what a search compares before it reads their content: the length where the
/// size or the content is a criterion, the name where the name is, and the last-modified time where
/// it is. Each group holds at least two different files.
/// </summary>
/// <param name="Length">The length every file shares, or null where the length was not compared.</param>
/// <param name="Name">The name every file shares, ignoring case, or null where the name was not compared.</param>
/// <param name="Modified">The last-modified time every file shares, or null where it was not compared.</param>
public sealed record CandidateGroup(long? Length, string? Name, DateTime? Modified, IReadOnlyList<DuplicateCandidate> Files);

/// <summary>
/// What a search left out as it read the tree and identified the files, counted so that a search
/// which found nothing in a place is never read as one that found nothing there because it did not
/// look.
/// </summary>
/// <param name="Links">Symbolic links, junctions and mount points: never followed, and never a file to match.</param>
/// <param name="Empty">Empty files, which are never matched: an empty file's name is its content.</param>
/// <param name="UnknownLength">Files whose length the scan could not establish.</param>
/// <param name="OnlyInTheCloud">
/// Cloud files not on this device, left out of a search that compares content because reading one
/// would download it.
/// </param>
/// <param name="Gone">Files no longer there as a file when they were identified.</param>
/// <param name="Unidentified">
/// Files Windows would not describe when they were identified. They may still be there, and are
/// never counted as gone.
/// </param>
public readonly record struct LeftOutFiles(
    int Links,
    int Empty,
    int UnknownLength,
    int OnlyInTheCloud,
    int Gone,
    int Unidentified)
{
    /// <summary>What two stages of one search left out between them.</summary>
    public static LeftOutFiles operator +(LeftOutFiles left, LeftOutFiles right) => new(
        left.Links + right.Links,
        left.Empty + right.Empty,
        left.UnknownLength + right.UnknownLength,
        left.OnlyInTheCloud + right.OnlyInTheCloud,
        left.Gone + right.Gone,
        left.Unidentified + right.Unidentified);
}
