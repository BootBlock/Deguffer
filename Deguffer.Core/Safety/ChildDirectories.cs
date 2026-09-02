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
/// <param name="Unreadable">
/// The root did not answer, so the two lists above describe nothing rather than describing a folder
/// with no children in it.
///
/// <para>A caller must not read this as a fourth kind of emptiness to ignore. Every caller of this
/// seam turns the children into a plan, and a plan built on a listing that never happened states
/// something about the machine that nobody established.</para>
/// </param>
public readonly record struct ChildDirectoryScan(
    IReadOnlyList<DirectoryInfo> Directories,
    IReadOnlyList<DirectoryInfo> Links,
    bool Unreadable);

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
    /// escapes the tree the caller reasoned about. A root that will not answer yields nothing *and
    /// says so*: §5.3 makes the refusal ordinary rather than an error, but ordinary is not the same
    /// as empty, and <see cref="ChildDirectoryScan.Unreadable"/> is the difference. Handing back a
    /// bare empty list would break the rule
    /// <see cref="Scanning.IDirectoryScanner.TryFindDirectoriesNamedAsync"/> states for the seam
    /// next door — that an empty list a caller cannot tell from "there are none" is not an answer.
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
            return new ChildDirectoryScan([], [], Unreadable: true);
        }

        return new ChildDirectoryScan(directories, links, Unreadable: false);
    }
}
