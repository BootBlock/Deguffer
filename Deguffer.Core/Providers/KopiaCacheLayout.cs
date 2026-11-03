using System.Text.Json;
using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where Kopia keeps its caches for this account, read from Kopia's own connection files rather than
/// from Kopia, and what it writes in each. Researched from Kopia's source (<c>repo/caching.go</c>,
/// <c>repo/local_config.go</c>, <c>repo/content/committed_read_manager.go</c>,
/// <c>app/public/config.js</c>): no Kopia was installed on the machine this was written against.
///
/// <para><b>Two folders, and the difference between them is the user's connection to their
/// backups.</b> Kopia's configuration is in <c>%APPDATA%\kopia</c>: one <c>.config</c> file each
/// repository it is connected to, holding the repository's address and the credentials to reach it,
/// and its password where the Windows credential store was not used. The caches are in
/// <c>%LOCALAPPDATA%\kopia</c>, one folder each connection, named by the first sixteen hexadecimal
/// digits of a hash, beside Kopia's logs. Only the second is ever read for something to remove.</para>
///
/// <para><b>A connection file names its own cache.</b> Kopia writes the cache folder into the file as
/// <c>caching.cacheDirectory</c>, relative to the file's folder where it can, and
/// <c>kopia cache info --path</c> prints exactly that, with <c>KOPIA_CACHE_DIRECTORY</c> put in its
/// place where that names a full path. KopiaUI connects to every <c>.config</c> file in the
/// configuration folder, and the command line to the one <c>KOPIA_CONFIG_PATH</c> names. A cache
/// folder in <c>%LOCALAPPDATA%\kopia</c> is found by its name as well, so one whose connection file
/// has gone is still found.</para>
/// </summary>
/// <param name="Caches">Every cache folder found, in display form.</param>
/// <param name="ConnectionFiles">Every connection file read, in display form.</param>
/// <param name="Notes">A cache folder a connection file names that is declined, and a folder Windows would not list.</param>
/// <param name="Unreadable">
/// The configuration folder or the local folder, where Windows would not list it. Neither is an empty
/// folder: a cache or a connection may be in it.
/// </param>
internal sealed partial record KopiaCacheLayout(
    IReadOnlyList<string> Caches,
    IReadOnlyList<string> ConnectionFiles,
    IReadOnlyList<PlanNote> Notes,
    IReadOnlyList<string> Unreadable)
{
    /// <summary>Names the connection file the command line uses (<c>--config-file</c>).</summary>
    public const string ConfigVariable = "KOPIA_CONFIG_PATH";

    /// <summary>Puts a cache folder in place of the one every connection file names, where it is a full path.</summary>
    public const string CacheVariable = "KOPIA_CACHE_DIRECTORY";

    private const string ConnectionSuffix = ".config";

    private const string DefaultConnection = "repository.config";

    /// <summary>What Kopia appends to a connection file's name for the file it keeps the password in (<c>passwordpersist</c>).</summary>
    private const string PasswordSuffix = ".kopia-password";

    /// <summary>A connection file is a few kilobytes. Anything far larger is not one.</summary>
    private const int MaximumConnectionBytes = 1024 * 1024;

    /// <summary>
    /// The folders Kopia caches into (<c>committed_read_manager.go</c>, <c>open.go</c>), each of which
    /// Kopia fills again from the repository. The format records Kopia caches as files beside them, and
    /// the <c>CACHEDIR.TAG</c>, are not listed, so they stay.
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        new ChildClassification(
            "contents",
            SafetyTier.RegenerableCache,
            "Copies of file contents Kopia read from the repository, which it reads again when it next needs them."),
        new ChildClassification(
            "metadata",
            SafetyTier.RegenerableCache,
            "Copies of the repository's folder listings and snapshot records, which Kopia reads again at its next run."),
        new ChildClassification(
            "indexes",
            SafetyTier.RegenerableCache,
            "The repository's indexes as Kopia unpacked them, which it reads and unpacks again at its next run."),
        new ChildClassification(
            "index-blobs",
            SafetyTier.RegenerableCache,
            "Copies of the repository's index files, which Kopia reads again at its next run."),
        new ChildClassification(
            "own-writes",
            SafetyTier.RegenerableCache,
            "Kopia's note of what it wrote to the repository lately, kept for storage that lists new files late. "
            + "Kopia's documentation says it is safe to remove."),
        new ChildClassification(
            "blob-list",
            SafetyTier.RegenerableCache,
            "A copy of the list of files in the repository, which Kopia lists again at its next run."),
        new ChildClassification(
            "server-contents",
            SafetyTier.RegenerableCache,
            "Copies of file contents a Kopia repository server sent, which Kopia asks for again when it next needs them."),
    ]);

    public static readonly IReadOnlyList<CacheLevel> Levels = [new CacheLevel(string.Empty, Children)];

    /// <summary>Kopia's configuration folder: the connection files, and any password not kept by Windows.</summary>
    public static string ConfigFolder(IUserEnvironment environment) => Path.Combine(environment.RoamingAppData, "kopia");

    /// <summary>The folder Kopia keeps its caches and its logs in.</summary>
    public static string LocalFolder(IUserEnvironment environment) => Path.Combine(environment.LocalAppData, "kopia");

    /// <summary>
    /// The file Kopia keeps <paramref name="connection"/>'s repository password in beside it, where the
    /// Windows credential store was not used.
    /// </summary>
    public static string PasswordFileOf(string connection) => connection + PasswordSuffix;

    /// <summary>Whether <paramref name="name"/> is a cache folder as Kopia names one in <see cref="LocalFolder"/>.</summary>
    public static bool IsCacheName(string name) => CacheName().IsMatch(name);

    /// <summary>Every cache folder this account's Kopia connections use, and every connection file read.</summary>
    public static KopiaCacheLayout Find(IUserEnvironment environment, ISystemDirectories machine, IVolumeInventory volumes)
    {
        var notes = new List<PlanNote>();
        var unreadable = new List<string>();
        var connections = ConnectionFilesOf(environment, unreadable, notes);
        var named = new List<string>();

        foreach (var connection in connections)
        {
            if (CacheNamedBy(connection) is { } cache)
            {
                named.Add(cache);
            }
        }

        // Kopia puts the variable in place of the cache only for a connection that caches at all.
        if (named.Count > 0
            && LongPath.Configured(ConfiguredFolder.Read(VariableReading.Current, CacheVariable, environment)) is { } moved)
        {
            named.Add(moved);
        }

        var local = LocalFolder(environment);
        var accountTemp = TempRoots.Resolve(environment, machine, volumes).AccountFolders;
        var caches = new List<string>();

        foreach (var cache in named.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A cache folder inside Kopia's own is Kopia's by construction. One elsewhere was written into a
            // file anything on the machine may have written, so it is judged as any setting is.
            if (!IsInside(local, cache)
                && ConfiguredFolder.WhyNotOwned(cache, environment, machine, volumes, accountTemp) is { } declined)
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"A Kopia connection names '{cache}' as its cache, and Deguffer will not treat that as Kopia's: "
                    + $"{declined} It is leaving it alone."));
                continue;
            }

            caches.Add(cache);
        }

        caches.AddRange(
            Listed(local, unreadable, notes)
                .Where(entry => entry is DirectoryInfo && IsCacheName(entry.Name) && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(entry => LongPath.Display(entry.FullName)));

        return new KopiaCacheLayout([.. caches.Distinct(StringComparer.OrdinalIgnoreCase)], connections, notes, unreadable);
    }

    /// <summary>
    /// Every <c>.config</c> file in the configuration folder, which KopiaUI connects to, and the one
    /// <see cref="ConfigVariable"/> names for the command line: a bare file name in the configuration
    /// folder, or a full path.
    /// </summary>
    private static IReadOnlyList<string> ConnectionFilesOf(IUserEnvironment environment, List<string> unreadable, List<PlanNote> notes)
    {
        var folder = ConfigFolder(environment);
        var files = Listed(folder, unreadable, notes)
            .Where(entry => entry is FileInfo && entry.Name.EndsWith(ConnectionSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(entry => LongPath.Display(entry.FullName))
            .ToList();

        var configured = ConfiguredFolder.Read(VariableReading.Current, ConfigVariable, environment)?.Trim();
        var named = string.IsNullOrEmpty(configured)
            ? Path.Combine(folder, DefaultConnection)
            : Path.GetFileName(configured) == configured
                ? Path.Combine(folder, configured)
                : LongPath.Configured(configured);

        // A file Windows will not describe may still be a connection, and it is listed so that it is asserted
        // to survive. Reading it then gives nothing, which names no cache.
        if (named is not null && LongPath.FileMayExist(named))
        {
            files.Add(named);
        }

        return [.. files.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The cache folder <paramref name="connection"/> records, resolved against the file's folder as
    /// Kopia resolves it, or null where the file records none or cannot be read.
    /// </summary>
    private static string? CacheNamedBy(string connection)
    {
        using var document = BoundedJsonFile.Read(connection, MaximumConnectionBytes);

        if (document is null
            || !document.RootElement.TryGetProperty("caching", out var caching)
            || caching.ValueKind != JsonValueKind.Object
            || BoundedJsonFile.StringProperty(caching, "cacheDirectory") is not { Length: > 0 } recorded)
        {
            return null;
        }

        // Configured resolves the '..' Kopia writes to reach the local folder from the roaming one, and
        // answers null for a value no full path can be made of.
        return LongPath.Configured(Path.Combine(Path.GetDirectoryName(connection)!, recorded));
    }

    /// <summary>
    /// What <paramref name="folder"/> holds, or nothing where it is not there. A folder Windows would not
    /// list is recorded and named instead, because it is not an empty folder (§5.3).
    /// </summary>
    private static IReadOnlyList<FileSystemInfo> Listed(string folder, List<string> unreadable, List<PlanNote> notes)
    {
        if (FolderEntries.Of(folder) is { } entries)
        {
            return entries;
        }

        unreadable.Add(folder);

        // "Could not list" says the folder is there, which a folder Windows would not describe never showed.
        notes.Add(LongPath.ProbeDirectory(folder) is PathPresence.Refused
            ? UnreadableRoot.UnreachedNote(folder)
            : UnreadableRoot.Note(folder));

        return [];
    }

    private static bool IsInside(string folder, string candidate) =>
        LongPath.Contains(folder, candidate) && !folder.Equals(candidate, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\A[0-9a-f]{16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CacheName();
}
