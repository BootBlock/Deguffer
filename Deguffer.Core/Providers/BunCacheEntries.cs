namespace Deguffer.Core.Providers;

/// <summary>
/// What <c>bun pm cache prune</c> makes of an entry in Bun's install cache, going by its name, ported
/// from <c>CacheEntryKind::from_name</c>
/// (<a href="https://github.com/oven-sh/bun/blob/bun-v1.4.4/src/install/PackageManager/PackageManagerDirectories.rs#L476-L513">PackageManagerDirectories.rs</a>).
/// </summary>
internal enum BunCacheEntryKind
{
    /// <summary>An extracted package, <c>name@version@@@1</c> and the git, GitHub and tarball forms. Prune removes it.</summary>
    Package,

    /// <summary>An <c>@scope</c> folder, holding that scope's packages. Prune looks inside it.</summary>
    Scope,

    /// <summary>A plain-named folder of version links. Prune removes only links in it whose target is gone.</summary>
    Index,

    /// <summary><c>links</c>, a <c>.git</c> clone, <c>@t@</c>, a dot-named staging folder, or anything not a folder. Prune keeps it.</summary>
    Other,
}

/// <summary>
/// The package folders <c>bun pm cache prune --max-age 0</c> removes from a cache, so a plan measures
/// what the command frees rather than the whole cache, whose <c>links</c> store, git clones and
/// manifests the command keeps.
///
/// <para>The walk is prune's own (<c>Prune::prune_dir</c> in
/// <a href="https://github.com/oven-sh/bun/blob/bun-v1.4.4/src/runtime/cli/pm_cache_prune_command.rs">pm_cache_prune_command.rs</a>):
/// the cache's top level, and one level inside each scope folder. A link is never a package, because
/// prune asks the entry's own kind and a link's is not a directory.</para>
/// </summary>
internal static class BunCacheEntries
{
    /// <summary>The global store projects link into. Prune keeps it, and the clean must leave it.</summary>
    public const string LinksName = "links";

    public static BunCacheEntryKind Classify(string name, bool isDirectory, bool inScope)
    {
        if (!isDirectory || name.Length == 0 || name[0] == '.')
        {
            return BunCacheEntryKind.Other;
        }

        if (name[0] == '@')
        {
            if (inScope || name.StartsWith("@t@", StringComparison.Ordinal))
            {
                return BunCacheEntryKind.Other;
            }

            return name.AsSpan(1).Contains('@') ? BunCacheEntryKind.Package : BunCacheEntryKind.Scope;
        }

        if (name.Contains('@', StringComparison.Ordinal))
        {
            return BunCacheEntryKind.Package;
        }

        return name == LinksName || name.EndsWith(".git", StringComparison.Ordinal)
            ? BunCacheEntryKind.Other
            : BunCacheEntryKind.Index;
    }

    /// <summary>Whether a folder directly in the cache is one prune removes, for Explore's declaration.</summary>
    public static bool IsPackage(string name) => Classify(name, isDirectory: true, inScope: false) is BunCacheEntryKind.Package;

    /// <summary>
    /// Every package folder prune would remove from <paramref name="cache"/>, or null where a folder
    /// it has to look in refused to be listed: a partial list would offer less than the command takes
    /// and protect nothing it missed.
    /// </summary>
    public static IReadOnlyList<string>? PackagesIn(string cache)
    {
        List<string> packages = [];

        return Collect(cache, inScope: false, packages) ? packages : null;
    }

    private static bool Collect(string folder, bool inScope, List<string> packages)
    {
        if (FolderEntries.Of(folder) is not { } entries)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            var isDirectory = entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

            switch (Classify(entry.Name, isDirectory, inScope))
            {
                case BunCacheEntryKind.Package:
                    packages.Add(Path.Combine(folder, entry.Name));
                    break;
                case BunCacheEntryKind.Scope when !Collect(Path.Combine(folder, entry.Name), inScope: true, packages):
                    return false;
            }
        }

        return true;
    }
}
