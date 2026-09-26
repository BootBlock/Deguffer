using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether a folder <c>settings.xml</c> names as the local repository holds what Maven puts there,
/// read from its contents.
///
/// <para><b>Asked only of a repository the settings moved.</b> The one under <c>.m2</c> is Maven's by
/// where it is. A folder the settings name is Maven's only if Maven has filled it, and it is removed
/// whole, so <c>&lt;localRepository&gt;${user.home}/Projects&lt;/localRepository&gt;</c> would
/// otherwise take the projects.</para>
///
/// <para><b>What counts.</b> Maven files every artifact under a folder per group, artifact and
/// version, and the version folder holds <c>&lt;artifact&gt;-&lt;version&gt;.pom</c> with a
/// <c>_remote.repositories</c> record beside it, whether the artifact was downloaded or installed.
/// Maven run with <c>-llr</c> writes no record, so metadata named <c>maven-metadata-*.xml</c> in the
/// artifact's folder counts in its place. Nothing but folders sits at the top of a repository, the
/// resolver's own <c>.locks</c> and <c>.meta</c> included. Researched against Maven Resolver's
/// source rather than measured.</para>
/// </summary>
internal static class MavenRepositoryEvidence
{
    /// <summary>
    /// How deep the walk goes looking for a version folder. A group nests one folder per segment of
    /// its name, so <c>org/apache/maven/plugins/maven-compiler-plugin/3.13.0</c> is six down, and eight
    /// covers the long group names in common use.
    /// </summary>
    private const int MaximumDepth = 8;

    /// <summary>
    /// How many folders the walk opens before it gives up. A repository shows a version folder within
    /// the first few; a folder that has not shown one by here is not laid out as a repository.
    /// </summary>
    private const int MaximumFolders = 2_000;

    /// <summary>
    /// Why <paramref name="folder"/> is not shown to be a Maven local repository, as the end of a
    /// sentence, or null where it is.
    /// </summary>
    public static string? WhyNotARepository(string folder, CancellationToken ct = default)
    {
        IReadOnlyList<FileSystemInfo> top;

        try
        {
            top = [.. new DirectoryInfo(LongPath.Extended(folder)).EnumerateFileSystemInfos()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing Windows will not list is evidence of anything.
            return "Windows would not list what is in it.";
        }

        if (top.OfType<FileInfo>().FirstOrDefault() is { } file)
        {
            return $"'{file.Name}' is in it, and Maven keeps nothing at the top of a local repository but folders.";
        }

        var pending = new Queue<(DirectoryInfo Folder, int Depth)>(
            top.OfType<DirectoryInfo>().Where(Followed).Select(group => (group, 1)));
        var opened = 0;

        while (pending.TryDequeue(out var next) && opened++ < MaximumFolders)
        {
            ct.ThrowIfCancellationRequested();

            if (IsVersionFolder(next.Folder))
            {
                return null;
            }

            if (next.Depth < MaximumDepth)
            {
                foreach (var child in Subfolders(next.Folder))
                {
                    pending.Enqueue((child, next.Depth + 1));
                }
            }
        }

        return "nothing in it is laid out as Maven lays out an artifact: a folder per group, artifact and version, "
            + "holding the artifact's .pom.";
    }

    /// <summary>
    /// A version folder: <c>&lt;artifact&gt;/&lt;version&gt;/&lt;artifact&gt;-&lt;version&gt;.pom</c>,
    /// with the record of where it came from, or the artifact's metadata one folder up.
    /// </summary>
    private static bool IsVersionFolder(DirectoryInfo version)
    {
        if (version.Parent is not { } artifact
            || !File.Exists(Path.Combine(version.FullName, $"{artifact.Name}-{version.Name}.pom")))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(version.FullName, "_remote.repositories"))
                || artifact.EnumerateFiles("maven-metadata-*.xml").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The folders inside <paramref name="folder"/> the walk goes on into, or none where Windows will
    /// not list it.
    /// </summary>
    private static IReadOnlyList<DirectoryInfo> Subfolders(DirectoryInfo folder)
    {
        try
        {
            return [.. folder.EnumerateDirectories().Where(Followed)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// A link is not walked through. What it leads to is somewhere else, and a repository found there
    /// says nothing about the folder the settings named.
    /// </summary>
    private static bool Followed(DirectoryInfo folder) =>
        !folder.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
