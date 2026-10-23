namespace Deguffer.Core.Exploring;

/// <summary>
/// What a reader knows about each node of an <see cref="ExploreTree"/> beyond its numbers, one bit
/// each.
///
/// <para>One byte a node rather than a <c>bool[]</c> per flag, because the file table's reader sizes
/// every array to the volume's record count before it reads one: four such arrays were four bytes a
/// record, and on an ordinary system volume of 2.4 million records that is seven megabytes this
/// saves.</para>
/// </summary>
[Flags]
internal enum ExploreNodeFlags : byte
{
    None = 0,

    /// <summary>See <see cref="ExploreTree.IsDirectory"/>.</summary>
    Directory = 1,

    /// <summary>See <see cref="ExploreTree.IsLink"/>.</summary>
    Link = 2,

    /// <summary>
    /// The node's size, or one somewhere beneath it, is not known. See
    /// <see cref="ExploreTree.HasUnknownSizeBelow"/>.
    /// </summary>
    SizeUnknown = 4,

    /// <summary>
    /// The reader filled this slot. The walk fills every one; the table leaves free and unreadable
    /// records empty, and <see cref="ExploreTree.Create"/> links only those present.
    /// </summary>
    Present = 8,
}
