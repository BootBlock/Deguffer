using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>
/// The backups in one folder, each file read once and remembered by its length and last write
/// until either changes.
///
/// <para>Nothing prunes the folder, so it only grows, and every listing would otherwise open, scan
/// and parse every file again. What is remembered only describes a file for the list: an import
/// reads the file afresh and checks those bytes (<see cref="RegistryBackups.ImportAsync"/>), so a
/// description out of date is never what a restore acts on.</para>
/// </summary>
internal sealed class RegistryBackupListing(string folder)
{
    /// <summary>
    /// The largest file read as a backup. An entry's export is a few kilobytes; anything near this is
    /// not one, and reading it whole would be the cost of a stranger's file.
    /// </summary>
    internal const int LongestFile = 4 * 1024 * 1024;

    private readonly Lock _gate = new();

    /// <summary>Every file the last listing read, keyed by its extended path.</summary>
    private Dictionary<string, Seen> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="Backup">What the file restores, or null where it is not a backup.</param>
    private readonly record struct Seen(long Length, DateTime Written, RegistryBackupFile? Backup);

    /// <summary>Every backup in the folder that names a key, newest first.</summary>
    /// <exception cref="OperationCanceledException">The listing was abandoned, and nothing it read is remembered.</exception>
    public IReadOnlyList<RegistryBackupFile> List(CancellationToken ct)
    {
        if (LongPath.ProbeDirectory(folder) is not PathPresence.Present)
        {
            return [];
        }

        // One listing at a time, so two cannot each read the same new files and race to remember them.
        lock (_gate)
        {
            var seen = new Dictionary<string, Seen>(_seen.Count, _seen.Comparer);

            try
            {
                // An enumerated FileInfo already carries the length and last write from the folder's
                // own listing, so a file remembered unchanged is never opened.
                foreach (var file in new DirectoryInfo(LongPath.Extended(folder)).EnumerateFiles("*.reg"))
                {
                    ct.ThrowIfCancellationRequested();

                    // A copy an interrupted restore left behind is not a backup the user took.
                    if (file.Name.StartsWith('.'))
                    {
                        continue;
                    }

                    if (_seen.TryGetValue(file.FullName, out var known) && known.Length == file.Length && known.Written == file.LastWriteTimeUtc)
                    {
                        seen.Add(file.FullName, known);
                    }
                    else if (TryDescribe(file, out var backup))
                    {
                        seen.Add(file.FullName, new Seen(file.Length, file.LastWriteTimeUtc, backup));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that stops listing part-way still yields the files it listed.
            }

            _seen = seen;

            return [.. seen.Values.Select(s => s.Backup).OfType<RegistryBackupFile>().OrderByDescending(b => b.Written)];
        }
    }

    /// <summary>What a backup file restores, read from the whole file.</summary>
    /// <param name="backup">What the file restores, or null where it is not a backup.</param>
    /// <returns>
    /// False where the file could not be read. Unlike a file that is not a backup, that is not
    /// remembered: a file held open by the export writing it or a scanner reading it is read again
    /// by the next listing rather than hidden until it next changes.
    /// </returns>
    internal static bool TryDescribe(FileInfo file, out RegistryBackupFile? backup)
    {
        backup = null;

        if (file.Length > LongestFile)
        {
            return true;
        }

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (RegistryFileContent.Parse(bytes) is { } content)
        {
            backup = new RegistryBackupFile(LongPath.Display(file.FullName), content.FirstKey, content.DisplayName, file.LastWriteTimeUtc, content.IsConfined);
        }

        return true;
    }
}
