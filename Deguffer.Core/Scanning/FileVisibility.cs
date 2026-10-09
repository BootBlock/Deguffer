namespace Deguffer.Core.Scanning;

/// <summary>
/// Whether a file or folder carries the hidden or the system attribute: the two Explorer leaves out
/// of a listing unless asked, and the two §7.4 lets a duplicate search leave out by default.
///
/// <para>Carried on its own rather than as the whole attribute word, because a tree keeps one value
/// per entry across millions of entries, and these two bits are all any reader of the tree asks
/// for. A byte an entry is what a column costs.</para>
/// </summary>
[Flags]
public enum FileVisibility : byte
{
    /// <summary>Neither attribute is set.</summary>
    Shown = 0,

    /// <summary><c>FILE_ATTRIBUTE_HIDDEN</c>.</summary>
    Hidden = 1,

    /// <summary><c>FILE_ATTRIBUTE_SYSTEM</c>.</summary>
    System = 2,
}

/// <summary>
/// Reads <see cref="FileVisibility"/> from a file's attributes, the one source both scan routes
/// have: the walk is handed them by the listing, and the file table keeps the same bits in
/// <c>$STANDARD_INFORMATION</c>.
/// </summary>
public static class FileVisibilities
{
    public static FileVisibility Of(FileAttributes attributes) =>
        (attributes.HasFlag(FileAttributes.Hidden) ? FileVisibility.Hidden : FileVisibility.Shown)
        | (attributes.HasFlag(FileAttributes.System) ? FileVisibility.System : FileVisibility.Shown);
}
