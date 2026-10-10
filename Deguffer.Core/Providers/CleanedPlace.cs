using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A place a provider's clean can delete files in, named without planning
/// (<see cref="ICleanupProvider.CleanedPlacesAsync"/>).
///
/// <para>Either a folder and everything in it, or, for a clean that finds what it deletes by name
/// below a folder (a project's <c>obj</c> or <c>node_modules</c> anywhere under a source folder),
/// every folder below that one with one of the names, and everything in each.</para>
/// </summary>
public sealed record CleanedPlace
{
    private CleanedPlace(string path, IReadOnlySet<string>? folderNames)
    {
        Path = path;
        FolderNames = folderNames;
    }

    /// <summary>The folder, or the file, the place starts at, as the provider names it.</summary>
    public string Path { get; }

    /// <summary>
    /// The names of the folders below <see cref="Path"/> that the clean deletes in, compared ignoring
    /// case, or null where it can delete anything at or below <see cref="Path"/>.
    /// </summary>
    public IReadOnlySet<string>? FolderNames { get; }

    /// <summary><paramref name="path"/> and everything in it.</summary>
    public static CleanedPlace Whole(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new CleanedPlace(path, folderNames: null);
    }

    /// <summary>
    /// Every folder below <paramref name="top"/>, at any depth, named one of <paramref name="names"/>,
    /// and everything in each. <paramref name="top"/> itself is not one of them.
    /// </summary>
    public static CleanedPlace FoldersNamed(string top, IEnumerable<string> names)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(top);
        ArgumentNullException.ThrowIfNull(names);

        HashSet<string> set = new(names, StringComparer.OrdinalIgnoreCase);

        if (set.Count == 0)
        {
            throw new ArgumentException("A place found by name needs at least one name.", nameof(names));
        }

        return new CleanedPlace(top, set);
    }

    /// <summary>
    /// Whether the clean can delete <paramref name="path"/>, judged from where the place starts:
    /// whether it is at or below <paramref name="start"/> and, for a place found by name, inside a
    /// folder with one of the names. Ignores case, which can only find more.
    /// </summary>
    /// <param name="start">Where the place starts, which is <see cref="Path"/> or another path to the same folder.</param>
    /// <param name="path">A full path, in either form <see cref="LongPath"/> produces.</param>
    public bool Holds(string start, string path) =>
        HoldsComparable(ReachedFolder.Comparable(start), ReachedFolder.Comparable(path));

    /// <summary>
    /// <see cref="Holds(string, string)"/> for a start and a path each already made comparable
    /// (<see cref="ReachedFolder.Comparable"/>), for a caller that asks about many of either and makes
    /// each comparable once, since that asks the file system.
    /// </summary>
    internal bool HoldsComparable(string top, string target)
    {
        if (!LongPath.Contains(top, target))
        {
            return false;
        }

        if (FolderNames is null)
        {
            return true;
        }

        var relative = LongPath.Relative(top, target);

        return relative != "."
            && relative.Split(System.IO.Path.DirectorySeparatorChar).Any(FolderNames.Contains);
    }

    /// <summary>Whether the clean can delete <paramref name="path"/>, by <see cref="Path"/> as the provider named it.</summary>
    public bool Holds(string path) => Holds(Path, path);
}
