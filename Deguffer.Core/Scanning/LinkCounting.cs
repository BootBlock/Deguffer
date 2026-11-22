namespace Deguffer.Core.Scanning;

/// <summary>
/// What a figure counts of a file that has more than one name (a hard link).
/// </summary>
public enum LinkCounting
{
    /// <summary>
    /// Each name counts the file, as a directory listing shows it and a removal of the folder takes
    /// it. Every entry the removal takes is counted. See <see cref="Mft.MftLink"/>.
    /// </summary>
    EachName,

    /// <summary>
    /// Only a file with one name counts, and no entries are counted: the bytes an eviction of the tree
    /// can give back, as <see cref="HardLinkAwareScanner"/> predicts them. A file with several names
    /// keeps its bytes while any name is left, so it counts nothing, even where every name is inside
    /// the tree.
    /// </summary>
    SoleName,
}
