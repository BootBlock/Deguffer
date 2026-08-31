namespace Deguffer.Core.Safety;

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
    /// The child directories of <paramref name="root"/>, never following a link out of it.
    ///
    /// A reparse point is never returned. A junction under a root points at a tree the caller has
    /// never classified, so deleting or descending through it escapes the tree the caller reasoned
    /// about. An unreadable root yields nothing, which §5.3 makes the normal answer rather than an
    /// error.
    ///
    /// <paramref name="root"/> may be given in either form. <see cref="LongPath.Extended"/> returns
    /// an already-prefixed path unchanged, so a walk that has extended once does not pay for it
    /// again per directory.
    /// </summary>
    public static IReadOnlyList<DirectoryInfo> Under(string root)
    {
        try
        {
            return
            [
                .. new DirectoryInfo(LongPath.Extended(root))
                    .EnumerateDirectories()
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint)),
            ];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }
}
