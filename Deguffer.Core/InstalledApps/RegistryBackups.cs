using System.Buffers;
using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>A backup file, and the key it restores.</summary>
/// <param name="Path">The file, in display form.</param>
/// <param name="KeyPath">The key the file's first section names, as it is stored.</param>
/// <param name="DisplayName">The entry's display name as the file records it, or null where it records none.</param>
public sealed record RegistryBackupFile(string Path, string KeyPath, string? DisplayName, DateTimeOffset Written)
{
    /// <summary>The entry this file restores, or null where it names no <c>Uninstall</c> entry.</summary>
    public UninstallKey? Key
    {
        get
        {
            // No scope's path is a prefix of another's, so at most one matches.
            foreach (var scope in UninstallScopes.All)
            {
                var prefix = scope.PhysicalPath() + @"\";

                if (KeyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var name = KeyPath[prefix.Length..];

                    return name.Length > 0 && !name.Contains('\\') ? new UninstallKey(scope, name) : null;
                }
            }

            return null;
        }
    }
}

/// <summary>What exporting or importing one backup came to.</summary>
/// <param name="Path">The file written or read, in display form, or null where none was written.</param>
public sealed record BackupOutcome(bool Succeeded, string? Path, string Message);

/// <summary>
/// The backups §7.3 takes before removing an entry, and restores on request, written and read by
/// <c>reg.exe</c> (§5.1: Windows' own tool writes the format Windows' own tool reads).
///
/// <para>Every key is named by its physical path in the 64-bit view, with <c>/reg:64</c>, so a
/// 32-bit entry's file names its <c>WOW6432Node</c> path outright. The file then restores what it
/// says whichever process imports it, including a double-click in Explorer.</para>
///
/// <para><c>reg.exe</c> exits 1 for a missing key and for a refusal alike, with localised text, so
/// nothing here reads its message to decide anything. An export succeeded only where it exited 0 and
/// the file is there with content.</para>
/// </summary>
public sealed class RegistryBackups(IProcessRunner runner, string folder, string regExe, TimeProvider time)
{
    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>The longest part of a file name taken from a key's name, well inside <c>MAX_PATH</c>.</summary>
    private const int LongestStem = 80;

    /// <summary>How far into a file its display name is looked for. An entry has a few dozen values.</summary>
    private const int LongestHeader = 500;

    public static RegistryBackups For(IUserEnvironment environment, ISystemDirectories directories, IProcessRunner runner) =>
        new(
            runner,
            Path.Combine(environment.LocalAppData, "Deguffer", "registry-backups"),
            Path.Combine(directories.WindowsDirectory, "System32", "reg.exe"),
            TimeProvider.System);

    /// <summary>Where backups are kept, in display form.</summary>
    public string Folder => folder;

    /// <summary>Export one entry, with the keys below it, to a new file.</summary>
    public Task<BackupOutcome> ExportAsync(UninstallKey key, CancellationToken ct) =>
        ExportAsync(key.PhysicalPath, $"{key.Scope} {key.Name}", ct);

    /// <summary>Export any key to a new file. A test exports a scratch key through this.</summary>
    internal async Task<BackupOutcome> ExportAsync(string keyPath, string label, CancellationToken ct)
    {
        if (keyPath.Contains('"'))
        {
            // reg.exe takes the key as one quoted argument, and a quote inside it cannot be carried.
            return new BackupOutcome(false, null, "The key's name holds a quotation mark, which reg.exe cannot be given.");
        }

        string file;

        try
        {
            Directory.CreateDirectory(LongPath.Extended(folder));
            file = NewFileName(label);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BackupOutcome(false, null, $"The backup folder {folder} could not be created: {ex.Message}");
        }

        // /y always: without it reg.exe waits on an overwrite prompt that nothing will answer.
        var outcome = await runner.RunAsync(regExe, $"export \"{keyPath}\" \"{file}\" /y /reg:64", ct).ConfigureAwait(false);

        if (outcome.Succeeded && new FileInfo(LongPath.Extended(file)) is { Exists: true, Length: > 0 })
        {
            return new BackupOutcome(true, file, $"Backed up to {file}.");
        }

        return new BackupOutcome(false, null, $"reg.exe could not back up the entry: {outcome.Message}");
    }

    /// <summary>Import a backup file, writing its keys back.</summary>
    public async Task<BackupOutcome> ImportAsync(RegistryBackupFile backup, CancellationToken ct)
    {
        var outcome = await runner.RunAsync(regExe, $"import \"{backup.Path}\" /reg:64", ct).ConfigureAwait(false);

        return outcome.Succeeded
            ? new BackupOutcome(true, backup.Path, $"Restored from {backup.Path}.")
            : new BackupOutcome(false, backup.Path, $"reg.exe could not restore the backup: {outcome.Message}");
    }

    /// <summary>Every backup in the folder that names a key, newest first.</summary>
    public IReadOnlyList<RegistryBackupFile> List()
    {
        var extended = LongPath.Extended(folder);

        if (LongPath.ProbeDirectory(folder) is not PathPresence.Present)
        {
            return [];
        }

        var backups = new List<RegistryBackupFile>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(extended, "*.reg"))
            {
                if (Describe(file) is { } backup)
                {
                    backups.Add(backup);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that stops listing part-way still yields the files it listed.
        }

        return [.. backups.OrderByDescending(b => b.Written)];
    }

    /// <summary>What a backup file restores, read from its first section, or null where it is not one.</summary>
    internal static RegistryBackupFile? Describe(string file)
    {
        try
        {
            // reg.exe writes UTF-16 with a byte-order mark, which the reader detects.
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? keyPath = null;
            string? displayName = null;

            for (var count = 0; count < LongestHeader && reader.ReadLine() is { } line; count++)
            {
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    if (keyPath is not null)
                    {
                        break;
                    }

                    keyPath = line[1..^1];
                }
                else if (keyPath is not null && line.StartsWith("\"DisplayName\"=\"", StringComparison.OrdinalIgnoreCase)
                    && line.EndsWith('"'))
                {
                    displayName = Unescape(line["\"DisplayName\"=\"".Length..^1]);
                }
            }

            return keyPath is null
                ? null
                : new RegistryBackupFile(LongPath.Display(file), keyPath, displayName, File.GetLastWriteTimeUtc(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Unescape(string value) => value.Replace("\\\"", "\"").Replace("\\\\", "\\");

    private string NewFileName(string label)
    {
        var stem = new StringBuilder(label.Length);

        foreach (var c in label)
        {
            stem.Append(InvalidFileNameChars.Contains(c) ? '_' : c);
        }

        var name = $"{time.GetLocalNow():yyyyMMdd-HHmmss} {stem.ToString(0, Math.Min(stem.Length, LongestStem)).Trim()}";
        var file = Path.Combine(folder, name + ".reg");

        for (var n = 2; LongPath.ProbeFile(file) is not PathPresence.Absent; n++)
        {
            file = Path.Combine(folder, $"{name} ({n}).reg");
        }

        return file;
    }
}
