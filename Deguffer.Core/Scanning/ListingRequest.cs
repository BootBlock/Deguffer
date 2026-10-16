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
public readonly record struct ListingRequest(ListingMemory Memory, int BufferBytes, bool FollowsLink, FileNames Files);
