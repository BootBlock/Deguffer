namespace Deguffer.Core.Safety;

/// <summary>
/// A path that names an entry on the disk, put in the form the rest of the code relies on without
/// changing which entry it names.
/// </summary>
public static partial class LongPath
{
    /// <summary>
    /// A path naming an entry the disk holds — a scanned item, a pick, something a walk enumerated —
    /// in the form <see cref="Configured"/> produces, but naming the same entry: nothing is trimmed
    /// from the value, and the last segment keeps the trailing dots and spaces Win32 would drop. Null
    /// when the value is not a full path, or names something Windows will not accept as one.
    ///
    /// <para><b>Safety, not spelling.</b> NTFS keeps a name such as <c>report.</c> when it is made
    /// through the <c>\\?\</c> namespace, and the scanner reports it as the disk holds it. Read as a
    /// configured value, the pick named <c>report</c>, so a removal was aimed at a sibling nobody
    /// picked, and the policy judged that sibling rather than the item.</para>
    /// </summary>
    public static string? Entry(string? path) => Qualified(path, Resolved);

    private static string? Qualified(string? value, Func<string, string> resolve)
    {
        if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value))
        {
            return null;
        }

        try
        {
            // A drive root keeps its separator, which is correct: "C:\" is the directory. A UNC root
            // does lose one, and both then have no containing directory at all — which is how the
            // callers that must refuse a whole volume come to refuse it.
            return Path.TrimEndingDirectorySeparator(resolve(Display(value)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Characters Windows will not accept in a path, so there is nothing here to point at.
            return null;
        }
    }

    /// <summary>
    /// <paramref name="path"/> with its separators made one, and its <c>.</c> and <c>..</c> segments
    /// resolved, and every name kept as it was spelled: what <see cref="Path.GetFullPath(string)"/>
    /// does to a fully qualified path, less what it does to names.
    ///
    /// <para><b>Win32 renames as it resolves.</b> It drops trailing dots and spaces from the last
    /// segment, and one trailing dot from any other: <c>C:\a\report.</c> resolves to
    /// <c>C:\a\report</c>, and <c>C:\a.\b</c> to <c>C:\a\b</c>, each a different entry when both are
    /// on the disk. NTFS keeps such names apart when they are made through the <c>\\?\</c> namespace,
    /// and every path this class hands to Windows goes through it, which resolves nothing, so a name
    /// kept here is the name acted on.</para>
    ///
    /// <para>A path that is not fully qualified, or already carries the device prefix, is answered as
    /// <see cref="Path.GetFullPath(string)"/> answers it: the first resolves against a working
    /// directory, and Windows resolves nothing in the second.</para>
    /// </summary>
    private static string Resolved(string path)
    {
        if (!Path.IsPathFullyQualified(path) || IsDeviceSpelling(path))
        {
            return Path.GetFullPath(path);
        }

        var root = Path.GetPathRoot(path)!.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var names = new List<string>();

        foreach (var name in path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            switch (name)
            {
                case "" or ".":
                    break;

                case "..":
                    // At the root, ".." stays at the root, as Windows has it.
                    if (names.Count > 0)
                    {
                        names.RemoveAt(names.Count - 1);
                    }

                    break;

                default:
                    names.Add(name);
                    break;
            }
        }

        var separator = Path.DirectorySeparatorChar.ToString();
        var resolved = names.Count == 0 ? root : Path.Join(root, string.Join(separator, names));

        return EndsWithSeparator(path) && !EndsWithSeparator(resolved) ? resolved + separator : resolved;
    }

    /// <summary>
    /// <paramref name="path"/> relative to <paramref name="relativeTo"/>, as
    /// <see cref="Path.GetRelativePath(string, string)"/> answers, keeping every name as spelled. A
    /// path on another root comes back whole, in display form.
    ///
    /// <para><b>The framework's own call renames.</b> It resolves both sides as Win32 does, so
    /// <c>app.\profile</c> below <c>C:\src</c> came back as <c>app\profile</c>, and a program working
    /// in <c>app.</c> was counted as using <c>app</c>. Asked of both sides in <c>\\?\</c> form, it
    /// resolves nothing in either.</para>
    /// </summary>
    public static string Relative(string relativeTo, string path)
    {
        var relative = Path.GetRelativePath(Extended(relativeTo), Extended(path));

        return Path.IsPathRooted(relative) ? Display(relative) : relative;
    }
}