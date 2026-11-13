using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// What one directory turned out to hold, as the walk found it.
///
/// <para>Valid only while the callback it was handed to runs. Each of the walk's workers lists into
/// the same three lists for every directory it reads, so a caller that keeps an entry copies it out.
/// A <see langword="ref"/> struct, so the compiler holds every caller to that: it cannot be stored in
/// a field, captured by a lambda or carried across an <see langword="await"/>.</para>
///
/// <para>The lists are spans rather than <see cref="IReadOnlyList{T}"/> because a walk enumerates
/// them once per directory, hundreds of thousands of times, and a <see langword="foreach"/> over the
/// interface boxes an enumerator each time. A span enumerates with no allocation (G4).</para>
/// </summary>
internal readonly ref struct DirectoryContents(
    string directory,
    ReadOnlySpan<WalkEntry> entries,
    ReadOnlySpan<WalkEntry> links,
    ReadOnlySpan<WalkEntry> reparseFiles,
    ListingEnd end,
    nint handle = 0)
{
    /// <summary>
    /// The directory listed, as the walk spelled it, in the extended form §6.3 requires. The same string
    /// every entry of the listing carries, so a caller can name a directory it could not list, which
    /// holds no entry to ask, and needs no copy of the path of its own carried down the tree.
    /// </summary>
    public string Directory { get; } = directory;

    /// <summary>
    /// The ordinary children — files and directories, in the order they were listed. Files are here only
    /// where the walk kept them, and named only where it asked: see <see cref="FileNames"/>. A link is not among
    /// them; see <see cref="Links"/> for why they are separated rather than mixed.
    /// </summary>
    public ReadOnlySpan<WalkEntry> Entries { get; } = entries;

    /// <summary>
    /// The children that are junctions, symbolic links or other name surrogates. Reported separately
    /// because a caller totalling bytes must not count them — their target keeps its own place on the
    /// volume — while a caller drawing the tree still has to show that they are there. Hiding them
    /// outright is what the walk used to do, and it makes a directory the user can see disappear.
    /// </summary>
    public ReadOnlySpan<WalkEntry> Links { get; } = links;

    /// <summary>
    /// The children that are files carrying a reparse point: a symbolic link to a file, and also a OneDrive
    /// placeholder or a deduplicated file, which are not links and whose content a removal would take.
    /// Kept out of <see cref="Entries"/> so a caller totalling bytes counts none of them, as the walk
    /// always has, and handed back so a caller asking a file's type still meets them. See
    /// <see cref="Safety.MailStore"/>.
    /// </summary>
    public ReadOnlySpan<WalkEntry> ReparseFiles { get; } = reparseFiles;

    /// <summary>
    /// How the listing ended, and why Windows would not list the directory where it would not. What was
    /// listed before a listing failed part-way is still in the three lists.
    /// </summary>
    public ListingEnd End { get; } = end;

    /// <summary>
    /// A handle open on the directory listed, valid only while the callback runs, as the lists are: the
    /// walk closes it when the callback returns or throws. Zero where the walk did not ask for it
    /// (<see cref="DirectoryHold.DuringCallback"/>), where the directory could not be opened, and for a
    /// test's listing, which opens nothing. A caller reading a file by name relative to it falls back to
    /// the file's full path where it is zero.
    /// </summary>
    public nint Handle { get; } = handle;

    /// <summary>
    /// <see cref="PathPresence.Present"/> where the listing was read to the end,
    /// <see cref="PathPresence.Absent"/> where the directory was gone by the time it was reached, and
    /// <see cref="PathPresence.Refused"/> where Windows would not list it.
    /// </summary>
    public PathPresence Presence => End.Presence;

    /// <summary>
    /// Whether the directory could not be listed to the end. §5.3 makes that ordinary rather than an
    /// error, so the walk still skips it silently — but a caller reporting a total needs to know the
    /// total is now a lower bound, and one that never hears about it cannot say so. A directory gone
    /// since its parent was listed counts too: its bytes were never read either.
    /// </summary>
    public bool WasRefused => Presence is not PathPresence.Present;

    /// <summary>How many children the listing found, across all three lists.</summary>
    public int Count => Entries.Length + Links.Length + ReparseFiles.Length;
}
