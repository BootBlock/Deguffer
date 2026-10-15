using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// What one directory turned out to hold, as the walk found it.
///
/// <para>Valid only while the callback it was handed to runs. Each of the walk's workers lists into
/// the same three lists for every directory it reads, so a caller that keeps an entry copies it out.
/// </para>
/// </summary>
/// <param name="Directory">
/// The directory listed, as the walk spelled it, in the extended form §6.3 requires. The same string
/// every entry of the listing carries, so a caller can name a directory it could not list, which
/// holds no entry to ask.
/// </param>
/// <param name="Entries">
/// The ordinary children — files and directories, in the order they were listed. A link is not among
/// them; see <paramref name="Links"/> for why they are separated rather than mixed.
/// </param>
/// <param name="Links">
/// The children that are junctions, symbolic links or other name surrogates. Reported separately
/// because a caller totalling bytes must not count them — their target keeps its own place on the
/// volume — while a caller drawing the tree still has to show that they are there. Hiding them
/// outright is what the walk used to do, and it makes a directory the user can see disappear.
/// </param>
/// <param name="ReparseFiles">
/// The children that are files carrying a reparse point: a symbolic link to a file, and also a OneDrive
/// placeholder or a deduplicated file, which are not links and whose content a removal would take.
/// Kept out of <paramref name="Entries"/> so a caller totalling bytes counts none of them, as the walk
/// always has, and handed back so a caller asking a file's type still meets them. See
/// <see cref="Safety.MailStore"/>.
/// </param>
/// <param name="Presence">
/// How the listing ended: <see cref="PathPresence.Present"/> where it was read to the end,
/// <see cref="PathPresence.Absent"/> where the directory was gone by the time it was reached, and
/// <see cref="PathPresence.Refused"/> where Windows would not list it. What was listed before a
/// listing failed part-way is still in the three lists.
/// </param>
internal readonly record struct DirectoryContents(
    string Directory,
    IReadOnlyList<WalkEntry> Entries,
    IReadOnlyList<WalkEntry> Links,
    IReadOnlyList<WalkEntry> ReparseFiles,
    PathPresence Presence)
{
    /// <summary>
    /// Whether the directory could not be listed to the end. §5.3 makes that ordinary rather than an
    /// error, so the walk still skips it silently — but a caller reporting a total needs to know the
    /// total is now a lower bound, and one that never hears about it cannot say so. A directory gone
    /// since its parent was listed counts too: its bytes were never read either.
    /// </summary>
    public bool WasRefused => Presence is not PathPresence.Present;

    /// <summary>How many children the listing found, across all three lists.</summary>
    public int Count => Entries.Count + Links.Count + ReparseFiles.Count;
}
