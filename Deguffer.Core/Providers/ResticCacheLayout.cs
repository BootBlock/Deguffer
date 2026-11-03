using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What restic writes in its cache folder, read from restic's source (<c>internal/backend/cache</c>)
/// rather than from an installation: no restic was installed on the machine this was written against.
///
/// <para><b>One folder a repository, named by the repository's identifier.</b> restic keeps the cache
/// for each repository it opens in a child of its cache folder named by the repository's 64-digit
/// hexadecimal identifier, holding a <c>version</c> file and the three folders it caches into:
/// <c>data</c>, <c>index</c> and <c>snapshots</c>. The cache folder itself holds a <c>CACHEDIR.TAG</c>
/// beside them, and, while <c>restic check</c> runs or after one was interrupted, a
/// <c>restic-check-cache-&lt;n&gt;</c> folder of its own.</para>
///
/// <para><b>restic's cleanup removes whole folders by name.</b> <c>restic cache --cleanup</c> lists the
/// cache folder, keeps the folders whose names match <see cref="IsTakenByCleanup"/>, and removes each
/// one whose modification time is older than <c>--max-age</c> days with Go's <c>os.RemoveAll</c>. It
/// keeps no record a removal by path would leave stale, and it reads no repository.</para>
/// </summary>
internal static partial class ResticCacheLayout
{
    /// <summary>Set by the user to move restic's cache folder (<c>cache.EnvDir</c>).</summary>
    public const string CacheVariable = "RESTIC_CACHE_DIR";

    /// <summary>The file restic writes in each repository's cache to record the layout it is in.</summary>
    public const string VersionFile = "version";

    /// <summary>
    /// The folders restic caches a repository's files into (<c>cacheLayoutPaths</c>). Anything else in a
    /// repository's cache is Tier 4 by construction.
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        new ChildClassification(
            "data",
            SafetyTier.RegenerableCache,
            "Copies of the repository's tree packs, which restic reads from the repository again when it next needs them."),
        new ChildClassification(
            "index",
            SafetyTier.RegenerableCache,
            "Copies of the repository's index files, which restic reads from the repository again at its next run."),
        new ChildClassification(
            "snapshots",
            SafetyTier.RegenerableCache,
            "Copies of the repository's snapshot records, which restic reads from the repository again at its next run."),
    ]);

    public static readonly IReadOnlyList<CacheLevel> Levels = [new CacheLevel(string.Empty, Children)];

    /// <summary>Whether <paramref name="name"/> is a repository's cache as restic names one.</summary>
    public static bool IsRepositoryCache(string name) => RepositoryCacheName().IsMatch(name);

    /// <summary>
    /// Whether restic's own cleanup would remove a folder of this name: a repository's cache, or a cache
    /// <c>restic check</c> made for itself (<c>validCacheDirName</c>).
    /// </summary>
    public static bool IsTakenByCleanup(string name) => CleanupName().IsMatch(name);

    /// <summary>
    /// Why <c>restic cache --cleanup</c> sent to <paramref name="root"/> now would remove something the plan
    /// does not offer, as a lower-case clause with no full stop, or null where everything it would take is
    /// a repository's cache holding only what restic writes there.
    ///
    /// <para>The cleanup removes each folder it takes whole, so a name Deguffer does not recognise inside
    /// one, a <c>restic check</c> cache, or a link anywhere it reaches would go with it unclassified.</para>
    /// </summary>
    public static string? WhyNotWhole(string root)
    {
        if (FolderEntries.Of(root) is not { } entries)
        {
            return $"Windows would not list {LongPath.Display(root)}";
        }

        foreach (var entry in entries.Where(entry => IsTakenByCleanup(entry.Name)))
        {
            var path = LongPath.Display(entry.FullName);

            if (IsLink(entry))
            {
                return $"{path} is a link to somewhere else";
            }

            if (entry is not DirectoryInfo)
            {
                continue;
            }

            if (!IsRepositoryCache(entry.Name))
            {
                return $"{path} is a cache 'restic check' made for itself, which Deguffer does not offer";
            }

            if (FolderEntries.Of(path) is not { } inside)
            {
                return $"Windows would not list {path}";
            }

            if (inside.FirstOrDefault(child => !IsWritten(child)) is { } stranger)
            {
                return $"{LongPath.Display(stranger.FullName)} is not something restic writes in a repository's cache";
            }
        }

        return null;
    }

    /// <summary>A folder restic caches into, or its version file, and neither of them a link.</summary>
    private static bool IsWritten(FileSystemInfo child) =>
        !IsLink(child) && child switch
        {
            DirectoryInfo => Children.IsDisposable(child.Name),
            _ => child.Name.Equals(VersionFile, StringComparison.OrdinalIgnoreCase),
        };

    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    [GeneratedRegex(@"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryCacheName();

    [GeneratedRegex(@"\A(?:[a-fA-F0-9]{64}|restic-check-cache-[0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex CleanupName();
}
