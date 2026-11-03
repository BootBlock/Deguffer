namespace Deguffer.Benchmark;

/// <summary>
/// The shape of a tree the removal routes build: <see cref="Depth"/> levels of folders below its root,
/// every folder above the deepest holding <see cref="Fanout"/> of them, and <see cref="Files"/> empty
/// files dealt across the root and every folder in turn.
///
/// <para>Empty files, because what a removal pays for a file is opening it by name and deleting it,
/// whatever it holds. A file with content would time the drive's writes while the tree is built, and
/// nothing more while it is removed.</para>
/// </summary>
/// <param name="ReadOnly">
/// Whether every file carries the read-only attribute, which a removal has to clear before Windows
/// lets it delete the file.
/// </param>
internal sealed record TreeShape(long Files, int Depth, int Fanout, bool ReadOnly = false)
{
    public const long MaximumFiles = 10_000_000;

    /// <summary>
    /// Deep enough for a path well past <c>MAX_PATH</c>, which is the depth §6.3 exists for. Each level
    /// adds at least three characters, so the deepest tree's files are more than 600 characters below
    /// its root.
    /// </summary>
    public const int MaximumDepth = 200;

    public const int MaximumFanout = 1000;

    /// <summary>
    /// The most folders a tree may have below its root. A depth and a fanout multiply out quickly, and
    /// a tree of a billion folders would fill the drive long before it was timed.
    /// </summary>
    public const long MaximumFolders = 1_000_000;

    /// <summary>
    /// The folders below the root, or null where there would be more than <see cref="MaximumFolders"/>.
    /// </summary>
    public static long? FoldersBelowRoot(int depth, int fanout)
    {
        long level = 1;
        long folders = 0;

        for (var i = 0; i < depth; i++)
        {
            level *= fanout;
            folders += level;

            if (folders > MaximumFolders)
            {
                return null;
            }
        }

        return folders;
    }

    /// <summary>The folders below the root. The shape's own values were accepted only where this is in range.</summary>
    public long Folders => FoldersBelowRoot(Depth, Fanout)!.Value;
}
