namespace Deguffer.Core.Safety;

/// <summary>
/// What one root's directory children turned out to be.
/// </summary>
/// <param name="Directories">Real directories, safe for a caller to classify and act on.</param>
/// <param name="Links">
/// Children that are junctions or symbolic links. Reported rather than discarded: a link under a
/// tool root is a child the user can see, so a plan that neither targets it nor mentions it is a
/// plan that quietly disagrees with the folder. It is never followed.
/// </param>
public readonly record struct ChildDirectoryScan(
    IReadOnlyList<DirectoryInfo> Directories,
    IReadOnlyList<DirectoryInfo> Links);

/// <summary>
/// The immediate directory children of a root, for §5.2's recognised-child classification and for
/// discovery walks.
///
/// One implementation because it carries two safety facts, and a hand-written copy is where one of
/// them goes missing. It had four copies and no test before this was extracted.
/// </summary>
public static class ChildDirectories
{
    /// <summary>
    /// The child directories of <paramref name="root"/>, with links separated out rather than
    /// followed.
    ///
    /// A link points at a tree the caller has never classified, so deleting or descending through it
    /// escapes the tree the caller reasoned about. An unreadable root yields nothing, which §5.3
    /// makes the normal answer rather than an error.
    ///
    /// <paramref name="root"/> may be given in either form. <see cref="LongPath.Extended"/> returns
    /// an already-prefixed path unchanged, so a walk that has extended once does not pay for it
    /// again per directory.
    /// </summary>
    public static ChildDirectoryScan Under(string root)
    {
        var directories = new List<DirectoryInfo>();
        var links = new List<DirectoryInfo>();

        try
        {
            foreach (var child in new DirectoryInfo(LongPath.Extended(root)).EnumerateDirectories())
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    links.Add(child);
                }
                else
                {
                    directories.Add(child);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Nothing rather than a partial view. A caller decides what a root holds from what it is
            // handed, so half a listing invites a plan that describes a folder nobody fully read.
            return new ChildDirectoryScan([], []);
        }

        return new ChildDirectoryScan(directories, links);
    }
}
