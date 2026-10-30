using Deguffer.Core.Safety;

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
    /// What prune would remove from <paramref name="cache"/> and what it would keep, or null where a
    /// folder it has to look in refused to be listed: a partial list would offer less than the command
    /// takes and protect nothing it missed.
    /// </summary>
    public static BunCacheContents? Of(string cache)
    {
        var contents = new BunCacheContents();

        return Collect(cache, inScope: false, contents) ? contents : null;
    }

    private static bool Collect(string folder, bool inScope, BunCacheContents contents)
    {
        if (FolderEntries.Of(folder) is not { } entries)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            var path = Path.Combine(folder, entry.Name);
            var isDirectory = entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

            switch (Classify(entry.Name, isDirectory, inScope))
            {
                // Every package Bun extracts carries its manifest, so a folder with no readable one is
                // something else that only shares the name shape, and prune would delete it all the
                // same. A refused probe is no evidence of a manifest.
                case BunCacheEntryKind.Package when LongPath.ProbeFile(Path.Combine(path, "package.json")) is not PathPresence.Present:
                    contents.Strangers.Add(path);
                    break;
                case BunCacheEntryKind.Package:
                    contents.Packages.Add(path);
                    break;
                case BunCacheEntryKind.Scope when !Collect(path, inScope: true, contents):
                    return false;
                case BunCacheEntryKind.Index when !HoldsOnlyLinks(path, contents):
                    return false;
                case BunCacheEntryKind.Other:
                    contents.Kept.Add(path);
                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// An index folder holds links to package folders and nothing else
    /// (<c>ExtractTarball::extract</c>). Prune removes every link whose package is gone, then the folder
    /// if that empties it, so a folder holding anything else is not Bun's index.
    /// </summary>
    private static bool HoldsOnlyLinks(string index, BunCacheContents contents)
    {
        if (FolderEntries.Of(index) is not { } entries)
        {
            return false;
        }

        if (entries.Any(entry => !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            contents.Strangers.Add(index);
        }

        return true;
    }
}

/// <summary>
/// One cache as prune would see it.
/// </summary>
internal sealed class BunCacheContents
{
    /// <summary>The package folders prune removes.</summary>
    public List<string> Packages { get; } = [];

    /// <summary>Everything prune keeps by its own rule, each of which must survive (§5.6).</summary>
    public List<string> Kept { get; } = [];

    /// <summary>
    /// Folders prune would delete or empty that do not look like Bun's own: a package-shaped name with
    /// no package manifest, or an index folder holding more than links.
    /// </summary>
    public List<string> Strangers { get; } = [];
}
