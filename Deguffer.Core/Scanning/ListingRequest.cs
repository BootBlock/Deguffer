namespace Deguffer.Core.Scanning;

/// <summary>How one directory is to be listed.</summary>
/// <param name="Memory">Where Windows writes the entries: the worker's own, reused for every directory it reads.</param>
/// <param name="BufferBytes">
/// How many bytes of entries each call asks Windows for, never more than <paramref name="Memory"/> holds.
/// </param>
/// <param name="FollowsLink">
/// Whether a link at the directory's name is listed through to its target. True for a root, which is
/// listed as it always was. False for a directory the walk descended into: its parent's listing said it
/// was no link, and one put there since is opened as itself, so nothing outside the tree is listed under
/// the child's path.
/// </param>
/// <param name="Files">What the listing keeps of each file.</param>
public readonly record struct ListingRequest(ListingMemory Memory, int BufferBytes, bool FollowsLink, FileNames Files)
{
    /// <summary>
    /// Where a listing from the disk leaves the directory's handle open once it has listed it, for
    /// the walk to close when its callback returns (<see cref="DirectoryHold.DuringCallback"/>). Null
    /// where the listing closes it at once. A listing with no handle, as a test's is, ignores it.
    /// </summary>
    internal Safety.ListedDirectoryHandle? Holder { get; init; }

    /// <summary>
    /// The name kept for an entry called <paramref name="name"/>, as <see cref="Files"/> asks: none for
    /// a file kept without one, lent from <see cref="Memory"/> where the listing lends its names, and
    /// otherwise a string of its own. One place, so a listing from the disk and one a test makes keep
    /// names alike.
    /// </summary>
    internal ListedName NameOf(ReadOnlySpan<char> name, bool isDirectory)
    {
        if (!isDirectory && !Files.Names(name))
        {
            return default;
        }

        return Files.Lends ? Memory.Names.Lend(name) : ListedName.Own(new string(name));
    }
}
