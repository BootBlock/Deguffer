using Deguffer.Core.Providers;

namespace Deguffer.Core.ModManagers;

/// <summary>
/// How much of one Mod Organizer 2 download folder is archives Mod Organizer 2 marks as installed, and as
/// uninstalled, which are what its own two bulk deletions take.
/// </summary>
/// <param name="Installed">The bytes in archives whose <c>.meta</c> marks them installed.</param>
/// <param name="Uninstalled">The bytes in archives whose <c>.meta</c> marks them uninstalled.</param>
/// <param name="Listed">False where Windows would not list the folder, so neither figure describes it.</param>
internal readonly record struct ModOrganizerArchives(long Installed, long Uninstalled, bool Listed)
{
    /// <summary>The extension Mod Organizer 2 gives a download's record, after the archive's own name.</summary>
    public const string MetaExtension = ".meta";

    /// <summary>The extension of a download that has not finished, which Mod Organizer 2 removes once it has.</summary>
    public const string UnfinishedExtension = ".unfinished";

    /// <summary>Far past any record Mod Organizer 2 writes, which holds a dozen short values.</summary>
    private const int MaximumMetaBytes = 64 * 1024;

    /// <summary>
    /// Each archive in <paramref name="folder"/> classified by its <c>.meta</c>, as Mod Organizer 2's
    /// download list classifies it (<c>DownloadManager::DownloadInfo::createFromMeta</c>): a paused download
    /// is neither, then <c>uninstalled</c> wins over <c>installed</c>. An archive with no record, or a record
    /// that will not be read, is counted in neither, because Mod Organizer 2 lists it as ready to install.
    /// </summary>
    public static ModOrganizerArchives Of(string folder, CancellationToken ct)
    {
        if (FolderEntries.Of(folder) is not { } entries)
        {
            return new ModOrganizerArchives(0, 0, Listed: false);
        }

        long installed = 0, uninstalled = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry is not FileInfo archive
                || archive.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || archive.Name.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase)
                || archive.Name.EndsWith(UnfinishedExtension, StringComparison.OrdinalIgnoreCase)
                || QtIniFile.Read(archive.FullName + MetaExtension, MaximumMetaBytes) is not { } meta
                || meta.IsTrue(QtIniFile.General, "paused"))
            {
                continue;
            }

            if (meta.IsTrue(QtIniFile.General, "uninstalled"))
            {
                uninstalled += archive.Length;
            }
            else if (meta.IsTrue(QtIniFile.General, "installed"))
            {
                installed += archive.Length;
            }
        }

        return new ModOrganizerArchives(installed, uninstalled, Listed: true);
    }
}
