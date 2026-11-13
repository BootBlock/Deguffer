namespace Deguffer.Core.Exploring;

/// <summary>
/// What <see cref="ExploreTree.Create"/> draws a scan's root by in place of what the root's slot
/// holds, written into the columns the tree keeps after any compaction, so never into columns a
/// reader is still writing.
/// </summary>
/// <param name="Name">
/// The root's name, numbered in the pool handed over with it: the path the user chose rather than
/// the name the disk holds for the folder.
/// </param>
/// <param name="Unread">
/// Whether the reader wrote nothing to the root's slot, so the root has no size, length, storage,
/// dates or visibility of its own whatever the slot holds. The file table's volume root is forced
/// present before its record is read, and a snapshot reads the slot's other columns where a parse
/// thread may be writing them.
/// </param>
internal readonly record struct DrawnRoot(int Name, bool Unread);
