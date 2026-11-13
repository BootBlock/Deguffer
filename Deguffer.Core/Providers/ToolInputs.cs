using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Everything a tool reads to give one answer about where it keeps something, as a provider declares
/// it: files, folders whose listing matters, and (always) the environment the tool is started with.
///
/// <para><b>A declaration must be complete for its tool.</b> A path left out is a change nobody
/// notices, and the remembered answer then names a folder the tool has stopped using. Each adopting
/// provider lists its tool's documented configuration sources in <c>docs/cache-locations.md</c>, and
/// <c>docs/todo/done/tool-location-memory.md</c> holds the inventories they were drawn from.</para>
///
/// <para><b>A file named by another file's content needs no entry of its own.</b> Where a setting in
/// one file moves another (npm's <c>prefix</c> moves its global configuration), the file holding the
/// setting is stamped, so editing it asks again. Where the tool names such a file in its answer, the
/// declaration is built from the answer, and the new file is stamped from then on.</para>
/// </summary>
internal sealed record ToolInputs
{
    /// <summary>Files or folders whose presence, size and times are read.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Folders whose entries are read as well as the folder itself.</summary>
    public IReadOnlyList<ListedFolder> Folders { get; init; } = [];

    /// <summary>These inputs and <paramref name="more"/>'s.</summary>
    public ToolInputs And(ToolInputs more) => new()
    {
        Files = [.. Files, .. more.Files],
        Folders = [.. Folders, .. more.Folders],
    };

    /// <summary>
    /// Each of <paramref name="names"/> in <paramref name="start"/> and in every folder above it, up to
    /// the drive root: where a tool looks for a project's configuration from the folder it runs in.
    /// </summary>
    public static IReadOnlyList<string> InEachFolderUp(string start, params IReadOnlyList<string> names)
    {
        var paths = new List<string>();

        for (var folder = start; !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            foreach (var name in names)
            {
                paths.Add(Path.Combine(folder, name));
            }
        }

        return paths;
    }

    /// <summary>
    /// The folder every tool Deguffer starts runs in (<see cref="ProcessRunner"/>), from which a tool
    /// looks for project configuration.
    /// </summary>
    public static string WorkingDirectory(IUserEnvironment environment) => ProcessRunner.WorkingDirectoryFor(environment);

    /// <summary>
    /// The path a tool opens for <paramref name="value"/>, a setting or a variable naming a file or a
    /// folder, or null where no path can be made of it, which the tool cannot open either.
    ///
    /// <para>A tool opens a relative value from the folder it runs in, as every process does, so that
    /// is what a declaration stamps. Read against Deguffer's own folder, or dropped for a default, the
    /// stamp would watch a path the tool never reads.</para>
    /// </summary>
    /// <param name="value">The value as the tool reads it, after any expansion of its own such as a leading <c>~</c>.</param>
    public static string? Opened(string value, IUserEnvironment environment)
    {
        // An empty value names no file, and the tool's own open of it fails.
        if (value.Length == 0)
        {
            return null;
        }

        try
        {
            return LongPath.Configured(Path.GetFullPath(value, WorkingDirectory(environment)));
        }
        catch (ArgumentException)
        {
            // A NUL, which no path can hold, so the tool's own open of it fails too.
            return null;
        }
    }

    /// <summary>What every declared input holds now, and what <paramref name="environment"/> holds.</summary>
    /// <param name="environment">Every variable the tool is started with.</param>
    public ToolFingerprint Read(IReadOnlyDictionary<string, string> environment)
    {
        var stamps = new List<InputStamp>(Files.Count + Folders.Count);

        foreach (var file in Files)
        {
            stamps.Add(InputStamp.Of(file));
        }

        foreach (var folder in Folders)
        {
            var self = InputStamp.Of(folder.Path);
            stamps.Add(self);

            if (self is { Presence: PathPresence.Present, IsDirectory: true })
            {
                List(folder.Path, folder.Depth, stamps);
            }
        }

        return new ToolFingerprint(stamps, environment);
    }

    /// <summary>
    /// Whether a folder above a declared input, up to the drive root, was written at or after
    /// <paramref name="fileTime"/>, or could not be read: what a file or a folder deleted, created by a
    /// rename or renamed away leaves behind, since what moved keeps no time of its own that says so. A
    /// folder holding a configuration file can be deleted whole, or moved in from elsewhere with its old
    /// times, and only the first folder above it that stayed put records the change.
    ///
    /// <para><b>Asked only when an answer is to be kept, and not part of the fingerprint.</b> The
    /// folders above the tools' working folder include the profile, where programs create and remove
    /// files all day, so comparing them on every pass would ask the tool again for nothing.</para>
    /// </summary>
    /// <param name="fileTime">The moment the tool was started, as a FILETIME.</param>
    public bool FoldersChangedSince(long fileTime)
    {
        var above = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Files.Concat(Folders.Select(static folder => folder.Path)))
        {
            for (var folder = Path.GetDirectoryName(path); folder is not null && above.Add(folder); folder = Path.GetDirectoryName(folder))
            {
                var stamp = InputStamp.Of(folder);

                if (stamp.Presence is PathPresence.Refused || stamp.Newest >= fileTime)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The entries of <paramref name="folder"/>, in name order so two readings of an unchanged folder
    /// compare equal, and those of each folder in it to <paramref name="depth"/> levels.
    ///
    /// <para><b>Each entry is read for itself, and the listing gives only its name.</b> The size and
    /// times a listing reports are NTFS's copy in the parent folder's index, which it brings up to
    /// date some time after the entry changes: a folder that just gained an entry, or a file still
    /// open for writing, lists with its old values. Stamped from the listing, a change could go
    /// unseen until the index caught up and an unchanged folder could read differently a moment
    /// later. One attribute read per entry, a few hundred a pass, is what an exact answer costs.</para>
    /// </summary>
    private static void List(string folder, int depth, List<InputStamp> stamps)
    {
        var names = FolderEntries.Of(folder, static (ref System.IO.Enumeration.FileSystemEntry entry) => entry.ToFullPath());

        if (names is null)
        {
            stamps.Add(InputStamp.Unreadable(folder));
            return;
        }

        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            var entry = InputStamp.Of(name);
            stamps.Add(entry);

            if (entry.IsDirectory && depth > 1)
            {
                List(entry.Path, depth - 1, stamps);
            }
        }
    }
}

/// <summary>A folder whose entries a tool's answer depends on, read to <paramref name="Depth"/> levels.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Depth">One for its own entries, two for those and the entries of each folder in it, and so on.</param>
internal readonly record struct ListedFolder(string Path, int Depth = 1);
