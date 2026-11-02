using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A <c>.vs</c> folder whose parent holds a Visual Studio solution, and the solutions it is kept for.
///
/// <para>The parent's solution file is what says Visual Studio made the folder. A folder called
/// <c>.vs</c> anywhere else is somebody's own and is never examined. The solutions also say which
/// folders inside it belong to a solution: one that no solution beside it names, as a renamed
/// solution leaves behind, is left alone whole, because nothing says what it is now.</para>
/// </summary>
/// <param name="Path">The <c>.vs</c> folder.</param>
/// <param name="Folder">The folder holding it and the solutions, which must survive (§5.6).</param>
/// <param name="SolutionFiles">Every solution file in <paramref name="Folder"/>, by full path.</param>
internal sealed record VisualStudioSolutionFolder(string Path, string Folder, IReadOnlyList<string> SolutionFiles)
{
    /// <summary>
    /// The name the user knows these solutions by: the solution's own where there is one, and the
    /// folder's where several share it.
    /// </summary>
    public string Name => SolutionFiles.Count == 1
        ? System.IO.Path.GetFileNameWithoutExtension(SolutionFiles[0])
        : System.IO.Path.GetFileName(Folder);

    /// <summary>
    /// The solution whose folder inside <c>.vs</c> is called <paramref name="name"/>, by the name the
    /// user knows it by, or null where no solution beside it writes to that name.
    /// </summary>
    public string? SolutionNamed(string name) =>
        SolutionFiles
            .Select(System.IO.Path.GetFileName)
            .OfType<string>()
            .FirstOrDefault(file => VisualStudioCacheLayout.SolutionFolderFor(file).Equals(name, StringComparison.OrdinalIgnoreCase))
            is { } solution
            ? System.IO.Path.GetFileNameWithoutExtension(solution)
            : null;

    /// <summary>
    /// <paramref name="candidate"/> as a solution's <c>.vs</c> folder, or null where it is not one: a
    /// link at it or at its parent, a parent that could not be listed, or no solution in the parent.
    /// </summary>
    public static VisualStudioSolutionFolder? TryRecognise(string candidate)
    {
        var path = candidate.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var folder = System.IO.Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(folder)
            || !System.IO.Path.GetFileName(path).Equals(VisualStudioCacheLayout.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A junction here would let a deletion escape the folder that was examined.
        if (LongPath.IsReparsePoint(path) || LongPath.IsReparsePoint(folder))
        {
            return null;
        }

        if (FolderEntries.Of(folder) is not { } entries)
        {
            return null;
        }

        List<string> solutions =
        [
            .. entries
                .OfType<FileInfo>()
                .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint) && SolutionFile.IsSolution(file.Name))
                .Select(file => System.IO.Path.Combine(folder, file.Name)),
        ];

        return solutions.Count == 0 ? null : new VisualStudioSolutionFolder(path, folder, solutions);
    }
}
