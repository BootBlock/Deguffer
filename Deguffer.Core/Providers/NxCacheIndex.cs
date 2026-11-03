using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What the folder beside an Nx cache says about which Nx reads the cache's index.</summary>
internal enum NxIndexReading
{
    /// <summary>
    /// The folder is not there. Whether that means no index depends on whether anything can move the
    /// index somewhere else: see <see cref="NxCacheIndex.AllowsRemoval"/>.
    /// </summary>
    Absent,

    /// <summary>The folder holds no database, so nothing names an entry in the cache by its hash.</summary>
    None,

    /// <summary>Only databases an Nx that treats a missing entry as a miss reads.</summary>
    Current,

    /// <summary>
    /// A database an older Nx reads, which would answer a removed entry as a hit and restore nothing.
    /// </summary>
    Older,

    /// <summary>The folder could not be read, or is a link, so which databases it holds is not known.</summary>
    Unknown,
}

/// <summary>
/// The databases in the folder that holds an Nx cache's index, and whether removing the cache is a
/// cache miss for every Nx that reads them.
///
/// <para><b>The index outlives the cache, and only a new enough Nx notices.</b> Since Nx 19 a row in
/// a SQLite database names each cache entry, and the entry is the folder <c>cache\&lt;hash&gt;</c>.
/// Nx 19 to 23.2 answer a lookup from the row alone: with the folder gone, the task is reported as
/// restored from the local cache, nothing is copied back, and the build goes on without its outputs.
/// Nx's own <c>nx reset --onlyCache</c> leaves the same state in those versions, because it removes
/// the cache folder and keeps the database. Nx 23.3.0 added the check, "A cache row can outlive its
/// artifact directory. Serving it as a hit would skip the task without restoring its outputs", in
/// <c>build_cached_result</c> in <c>packages/nx/src/native/cache/cache.rs</c>.</para>
///
/// <para><b>The file name says which Nx reads it.</b> Nx 19 to 22 name the database
/// <c>&lt;machine&gt;.db</c>. Nx 23.0 added the schema version to the name, <c>-v3.db</c> to 23.2 and
/// <c>-v4.db</c> from 23.3.0, the release that added the check; 24.0.3 writes <c>-v6.db</c>. An Nx
/// opens only the file its own version names, so a database named <c>-v4</c> or later is read only
/// by an Nx that treats a missing entry as a miss. A folder holding no database has no rows, so it
/// is safe too. Anything else, a <c>-v3</c> or an unversioned file or a name this does not parse,
/// could be read by an Nx that restores nothing, and is the one case where removing the cache is a
/// broken build rather than a slower one.</para>
/// </summary>
/// <param name="Reading">What the databases say.</param>
/// <param name="Files">
/// Every database file in the folder by full path, with SQLite's journal files beside them. Nx holds
/// them open while it runs a task or its daemon runs, so they are the files the in-use check asks
/// Windows about.
/// </param>
internal sealed partial record NxCacheIndex(NxIndexReading Reading, IReadOnlyList<string> Files)
{
    /// <summary>The first schema version whose Nx treats a cache row without its folder as a miss.</summary>
    public const int FirstVersionThatChecks = 4;

    /// <summary>
    /// Whether every Nx that reads this index answers a removed entry as a miss, which is when the
    /// cache it indexes may go.
    /// </summary>
    /// <param name="absentIsEmpty">
    /// Whether a folder that is not there means no index. True only where nothing moves the index:
    /// a workspace's index follows <c>NX_WORKSPACE_DATA_DIRECTORY</c>, which a shell can set where
    /// Deguffer cannot see it, so a missing <c>workspace-data</c> may mean an index elsewhere.
    /// </param>
    public bool AllowsRemoval(bool absentIsEmpty) =>
        Reading is NxIndexReading.None or NxIndexReading.Current
        || (absentIsEmpty && Reading == NxIndexReading.Absent);

    /// <summary>Read the databases in <paramref name="folder"/>.</summary>
    public static NxCacheIndex Read(string folder)
    {
        switch (LongPath.ProbeDirectory(folder))
        {
            case PathPresence.Absent:
                return new NxCacheIndex(NxIndexReading.Absent, []);

            case PathPresence.Refused:
                return new NxCacheIndex(NxIndexReading.Unknown, []);
        }

        // A link would make the folder somewhere else's, and what is there was never classified.
        if (LongPath.IsReparsePoint(folder) || FolderEntries.Of(folder) is not { } entries)
        {
            return new NxCacheIndex(NxIndexReading.Unknown, []);
        }

        var reading = NxIndexReading.None;
        List<string> files = [];

        foreach (var entry in entries)
        {
            if (Database().Match(entry.Name) is not { Success: true } database)
            {
                continue;
            }

            files.Add(Path.Combine(folder, entry.Name));

            var current = entry is FileInfo
                && database.Groups["version"].Success
                && int.TryParse(database.Groups["version"].ValueSpan, out var version)
                && version >= FirstVersionThatChecks;

            reading = current && reading != NxIndexReading.Older ? NxIndexReading.Current : NxIndexReading.Older;
        }

        return new NxCacheIndex(reading, files);
    }

    /// <summary>
    /// A SQLite database Nx names, or one of the journal files SQLite keeps beside it. The version is
    /// absent from the name Nx 19 to 22 write.
    /// </summary>
    [GeneratedRegex(@"^(?:.+?-v(?<version>\d{1,9})|.+?)\.db(?:-wal|-shm|-journal)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Database();
}
