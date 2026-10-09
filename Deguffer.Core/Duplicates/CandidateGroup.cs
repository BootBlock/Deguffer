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
/// <param name="SizeOnDisk">
/// What it occupied on disk when it was identified, as Windows reports it, which removing it may free
/// less than: clusters it shares through block cloning or deduplication are counted in full, because
/// Windows does not report a share, and a copy in the Recycle Bin frees nothing until the bin is
/// emptied (§7.4).
/// </param>
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
    long SizeOnDisk,
    DateTime Modified,
    FileStorage Storage,
    LocationRole Role)
{
    /// <summary>Whether the file has hard links, so removing one name would free nothing.</summary>
    public bool HasSeveralNames => NameCount > 1;

    /// <summary>
    /// The route the file's volume identifies its files by, which is the route it is identified by
    /// again before its content is read: the other gives the volume's serial number at another
    /// width, so the same file would read as a different one.
    /// </summary>
    internal IdentityRoute Route { get; init; }

    /// <summary>
    /// The volume the file is on, as the search resolved the location it was found in: the one
    /// answer every later stage reads, for the disk its content is read from and for whether a link
    /// can exist on the way to it, rather than each asking the machine again for each file.
    /// </summary>
    internal LocalVolume Volume { get; init; }

    /// <summary>
    /// Its attributes when it was identified, which a removal finds unchanged before the copy goes,
    /// as it finds its identity, length and last-modified time (§7.4).
    /// </summary>
    internal FileAttributes Attributes { get; init; }

    /// <summary>
    /// Whether no link can be anywhere on the file's path: its volume answered that it supports no
    /// reparse points, and the path starts at that volume's own root rather than in a folder of the
    /// volume it is mounted in, whose folders can be links. Only then may the file be opened by its
    /// path where Windows will not open it by its number.
    /// </summary>
    internal bool NoLinkOnItsPath =>
        Volume.CannotHoldLinks && HostVolume.IsMountPoint(Volume.RootPath, System.IO.Path.GetPathRoot(Path)!);
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
/// would download it: as the scan or the identification saw them, or as Windows said immediately
/// before the content would have been read.
/// </param>
/// <param name="Gone">Files no longer there as a file when they were identified, or when their content was to be read.</param>
/// <param name="Unidentified">
/// Files Windows would not describe when they were identified, or again before their content was
/// read. They may still be there, and are never counted as gone.
/// </param>
/// <param name="ReadFailed">
/// Files whose content could not be read: held by another program that would not share it, refused
/// by an access rule, or failing as they were read. They may still be there, and are never counted
/// as gone.
/// </param>
/// <param name="Changed">
/// Files that were not the file the search identified by the time their content was read, or that
/// changed while it was: another file at the path, or a different length, last-modified time or
/// attributes. Their bytes cannot be said to be the file's.
/// </param>
public readonly record struct LeftOutFiles(
    int Links,
    int Empty,
    int UnknownLength,
    int OnlyInTheCloud,
    int Gone,
    int Unidentified,
    int ReadFailed,
    int Changed)
{
    /// <summary>What two stages of one search left out between them.</summary>
    public static LeftOutFiles operator +(LeftOutFiles left, LeftOutFiles right) => new(
        left.Links + right.Links,
        left.Empty + right.Empty,
        left.UnknownLength + right.UnknownLength,
        left.OnlyInTheCloud + right.OnlyInTheCloud,
        left.Gone + right.Gone,
        left.Unidentified + right.Unidentified,
        left.ReadFailed + right.ReadFailed,
        left.Changed + right.Changed);
}
