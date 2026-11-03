using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where Coursier's caches that are not downloads are on this machine, and whether a folder overlaps
/// one of them.
///
/// <para>One answer for the plan, for Explore and for the duplicate search, because each asks the same
/// question of a different path: a download folder that holds a kept cache, or sits in one, is not a
/// download folder. A second copy of the rule is where one of the three stops applying it. The
/// question is asked of the folders as <see cref="ReachedFolder"/> reaches them, not of their text,
/// because each location is a setting, and two settings can name one folder through different
/// letters.</para>
/// </summary>
internal sealed class CoursierKeptCaches
{
    /// <summary>
    /// Coursier's caches that are not downloads, each with the variable that moves it, the folder it has
    /// in the cache home where that is unset, and the entries it fills.
    ///
    /// <para><c>arc</c> and <c>priv</c> are archive caches, which file each extraction under its address
    /// as the artefact cache does, so their entries are the two schemes. <c>jvm</c> names each runtime by
    /// its id and <c>digest</c> each layer by its digest, so neither has a name a refusal could list.</para>
    /// </summary>
    public static readonly IReadOnlyList<KeptCache> All =
    [
        new(
            "COURSIER_ARCHIVE_CACHE",
            "arc",
            "Archives Coursier extracted, among them the Java runtimes 'cs java' and 'cs setup' installed. "
            + "JAVA_HOME may point into it.",
            ["https", "http"]),
        new(
            "COURSIER_JVM_CACHE",
            "jvm",
            "Java runtimes that older Coursier releases installed. JAVA_HOME may point into it.",
            []),
        new(
            "COURSIER_DIGEST_BASED_CACHE",
            "digest",
            "Container image layers, among them ones 'cs docker build' made on this computer, which exist "
            + "nowhere else.",
            []),
        new(
            "COURSIER_PRIVILEDGED_ARCHIVE_CACHE",
            "priv",
            "Container image layers Coursier unpacked with elevated rights, which a running container may be "
            + "built on.",
            ["https", "http"]),
    ];

    private readonly IReadOnlyList<KeptLocation> _locations;
    private readonly IVolumeInventory _volumes;

    private CoursierKeptCaches(IReadOnlyList<KeptLocation> locations, IVolumeInventory volumes)
    {
        _locations = locations;
        _volumes = volumes;
    }

    /// <summary>
    /// Where each kept cache is, wherever its variable puts it, including a folder Deguffer declines to
    /// examine as Coursier's: asserting that something survived needs no claim that the folder is the
    /// tool's. A relative value names nowhere Deguffer can find, so that cache is left out.
    /// </summary>
    public static CoursierKeptCaches Resolve(
        string cacheHome,
        IUserEnvironment environment,
        ISystemDirectories system,
        IVolumeInventory volumes) =>
        new(
        [
            .. All
                .Select(cache => (Cache: cache, Setting: ConfiguredFolder.FromVariable(
                    cache.Variable, Path.Combine(cacheHome, cache.Name), environment, system, volumes)))
                .Select(found => (found.Cache, found.Setting, Path: found.Setting.Value is null ? found.Setting.Folder : found.Setting.Named))
                .Where(found => found.Path is not null)
                .Select(found => new KeptLocation(
                    found.Cache, found.Setting, found.Path!, ReachedFolder.At(found.Path!, volumes))),
        ],
        volumes);

    /// <summary>Each kept cache's location and what it holds, for §5.6 and the plan's notes.</summary>
    public IReadOnlyList<(string Path, string Reason)> Protected =>
        [.. _locations.Select(location => (location.Path, location.Cache.Reason))];

    /// <summary>The kept cache <paramref name="path"/> holds or is inside, or null where it overlaps none.</summary>
    public (string Path, string Reason)? Overlapping(string path)
    {
        var folder = ReachedFolder.At(path, _volumes);

        return _locations.FirstOrDefault(location => location.Reached.Holds(folder) || folder.Holds(location.Reached)) is { } found
            ? (found.Path, found.Cache.Reason)
            : null;
    }

    /// <summary>Whether <paramref name="path"/> is one of the kept caches itself.</summary>
    public bool IsKept(string path)
    {
        var folder = ReachedFolder.At(path, _volumes);

        return _locations.Any(location => location.Reached.Holds(folder) && folder.Holds(location.Reached));
    }

    /// <summary>
    /// Each kept cache a variable moved to a folder Deguffer accepts as Coursier's, refusing everything in
    /// it. The default ones are inside the cache home, which declares its own way down.
    /// </summary>
    public IEnumerable<ToolRoot> MovedRoots =>
        _locations
            .Where(location => location.Setting.Value is not null && location.Setting.Folder is not null)
            .Select(location => ToolRoot.Folders(location.Path, location.Cache.Reason, static _ => false));

    /// <summary>
    /// Each kept cache a variable moved to a folder Deguffer declines, such as Documents, refusing only
    /// the entries Coursier fills there: the rest of the folder is the user's, and refusing it would read
    /// the whole of it as Coursier's. See <see cref="ToolRoot.Sparing"/>.
    /// </summary>
    public IEnumerable<ToolRoot> DeclinedRoots =>
        _locations
            .Where(location => location.Setting.DeclinedFolder is not null && location.Cache.Entries.Count > 0)
            .Select(location => ToolRoot.Sparing(location.Path, location.Cache.Reason, location.Cache.Entries));

    private sealed record KeptLocation(KeptCache Cache, ConfiguredFolder.Setting Setting, string Path, ReachedFolder Reached);
}

/// <summary>One of Coursier's caches that holds something other than downloads.</summary>
/// <param name="Variable">The environment variable that moves it, and names the folder itself.</param>
/// <param name="Name">Its folder in the cache home where the variable is unset.</param>
/// <param name="Reason">What it holds, written for the user.</param>
/// <param name="Entries">The names Coursier gives what it fills the folder with, where they are fixed.</param>
internal sealed record KeptCache(string Variable, string Name, string Reason, IReadOnlyList<string> Entries);
