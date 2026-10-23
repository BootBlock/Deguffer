using System.Security;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Startup;

/// <summary>A file's size and last write, which §7.5's check compares before and after a change.</summary>
public sealed record FileStamp(long Length, DateTime LastWriteUtc);

/// <summary>One file in a Startup folder.</summary>
/// <param name="Name">Its file name, which is also the name its <c>StartupApproved</c> value has.</param>
public sealed record FolderItem(string Name, string Path, FileStamp Stamp);

/// <summary>Every item one Startup folder holds.</summary>
/// <param name="Folder">The folder, or null where Windows would not say where it is.</param>
public sealed record FolderItems(string? Folder, PathPresence Presence, IReadOnlyList<FolderItem> Items);

/// <summary>
/// Lists the two Startup folders (§7.5), each where Windows says it is, through the environment
/// seams so a test lists a folder it built.
/// </summary>
public sealed class StartupFolderReader(IUserEnvironment user, ISystemDirectories system)
{
    /// <summary>The folders of the account this process runs as (G5).</summary>
    public static StartupFolderReader Current { get; } = new(UserEnvironment.Current, SystemDirectories.Current);

    /// <summary>The folder's own settings, which Windows does not start.</summary>
    private const string FolderSettings = "desktop.ini";

    /// <summary>Where <paramref name="source"/>'s items are, or null where Windows would not say.</summary>
    public string? FolderOf(StartupSource source) => source switch
    {
        StartupSource.UserStartupFolder => user.StartupFolder,
        StartupSource.CommonStartupFolder => system.CommonStartupFolder,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a Startup folder."),
    };

    /// <summary>Every item in <paramref name="source"/>'s folder. Never throws for a refusal.</summary>
    public FolderItems List(StartupSource source)
    {
        if (FolderOf(source) is not { } folder)
        {
            return new FolderItems(null, PathPresence.Refused, []);
        }

        try
        {
            var directory = new DirectoryInfo(LongPath.Extended(folder));

            if (!directory.Exists)
            {
                return new FolderItems(folder, LongPath.ProbeDirectory(folder), []);
            }

            var items = new List<FolderItem>();

            foreach (var file in directory.EnumerateFiles())
            {
                if (!file.Name.Equals(FolderSettings, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new FolderItem(file.Name, Path.Combine(folder, file.Name), new FileStamp(file.Length, file.LastWriteTimeUtc)));
                }
            }

            return new FolderItems(folder, PathPresence.Present, items);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new FolderItems(folder, PathPresence.Refused, []);
        }
    }

    /// <summary>
    /// One item's stamp read again, or null where it is not there. <see cref="PathPresence.Refused"/>
    /// comes back as its own answer, never as an item that has gone.
    /// </summary>
    public (PathPresence Presence, FileStamp? Stamp) Stamp(StartupSource source, string name)
    {
        if (FolderOf(source) is not { } folder)
        {
            return (PathPresence.Refused, null);
        }

        var path = Path.Combine(folder, name);

        try
        {
            var file = new FileInfo(LongPath.Extended(path));

            return file.Exists
                ? (PathPresence.Present, new FileStamp(file.Length, file.LastWriteTimeUtc))
                : (LongPath.ProbeFile(path), null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return (PathPresence.Refused, null);
        }
    }
}
