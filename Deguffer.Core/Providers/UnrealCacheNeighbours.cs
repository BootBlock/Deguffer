using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What sits beside Unreal's caches and must survive a clean of them (§5.6), read by listing the
/// folders, since the engine versions and anything else there cannot be named in advance.
/// </summary>
internal static class UnrealCacheNeighbours
{
    /// <summary>
    /// Every engine version's folder beside <c>Common</c>, and the settings inside it, for §5.6.
    ///
    /// <para>Listed, which a declared root otherwise never is, because the version names are the
    /// user's installed engines and cannot be written down in advance. The listing names survivors
    /// and nothing else: no entry found here can become a target.</para>
    /// </summary>
    public static IEnumerable<(string RelativePath, string Reason)> EngineVersionFolders(string engine)
    {
        var children = ChildDirectories.Under(engine);

        return children.Directories.Concat(children.Links)
            .Select(child => child.Name)
            .Where(name => !name.Equals("Common", StringComparison.OrdinalIgnoreCase))
            .SelectMany(name => new[]
            {
                (name, $"Unreal Engine {name}'s own folder, with its settings and crash reports."),
                (Path.Combine(name, "Saved", "Config"), $"Your editor settings for Unreal Engine {name}."),
            });
    }

    /// <summary>
    /// Every entry in <paramref name="folder"/> other than those <paramref name="named"/> elsewhere,
    /// for §5.6: the unrecognised neighbours of a cache, which an over-broad rule would take with it.
    /// Listed for the reason <see cref="EngineVersionFolders"/> is, and never targeted. A folder that
    /// is not there, or will not be listed, names nothing.
    /// </summary>
    public static IEnumerable<(string RelativePath, string Reason)> Of(
        string root,
        string folder,
        params string[] named)
    {
        List<string> entries;

        try
        {
            entries =
            [
                .. new DirectoryInfo(LongPath.Extended(Path.Combine(root, folder)))
                    .EnumerateFileSystemInfos()
                    .Select(entry => entry.Name)
                    .Where(name => !named.Contains(name, StringComparer.OrdinalIgnoreCase)),
            ];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }

        return entries.Select(name => (
            Path.Combine(folder, name),
            $"'{name}' sits beside Unreal's cache and is not recognised as part of it, so it is left alone."));
    }
}
