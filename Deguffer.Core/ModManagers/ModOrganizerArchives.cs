using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.ModManagers;

/// <summary>
/// How much of one Mod Organizer 2 download folder is archives Mod Organizer 2 lists as installed, and as
/// uninstalled, which are what its own two bulk deletions take, and how much it hides from its list.
/// </summary>
/// <param name="Installed">The bytes in listed archives whose <c>.meta</c> marks them installed.</param>
/// <param name="Uninstalled">The bytes in listed archives whose <c>.meta</c> marks them uninstalled.</param>
/// <param name="Hidden">
/// The bytes in archives whose <c>.meta</c> marks them <c>removed</c>: hidden from the download list, as its
/// Hide commands and its setting to hide a download once installed leave them. The bulk deletions work
/// through the list, so they reach a hidden archive only while the list shows hidden downloads.
/// </param>
/// <param name="Listed">False where Windows would not list the folder, so no figure describes it.</param>
internal readonly record struct ModOrganizerArchives(long Installed, long Uninstalled, long Hidden, bool Listed)
{
    /// <summary>The extension Mod Organizer 2 gives a download's record, after the archive's own name.</summary>
    public const string MetaExtension = ".meta";

    /// <summary>The extension of a download that has not finished, which Mod Organizer 2 removes once it has.</summary>
    public const string UnfinishedExtension = ".unfinished";

    /// <summary>Far past any record Mod Organizer 2 writes, which holds a dozen short values.</summary>
    private const int MaximumMetaBytes = 64 * 1024;

    /// <summary>
    /// Enough records read at once to keep a spinning disk's queue full, where each small cold read costs
    /// milliseconds and a heavily modded game's download folder holds thousands. Each read stays small.
    /// </summary>
    private const int RecordsAtOnce = 8;

    /// <summary>Where one archive's record puts it, as Mod Organizer 2's download list reads the record.</summary>
    private enum Listing
    {
        Neither,
        Installed,
        Uninstalled,
        Hidden,
    }

    /// <summary>
    /// <see cref="Of"/> for each of <paramref name="folders"/>, on a thread of the read's own rather than
    /// the caller's. A caller that measures the same folders starts this first and awaits both, so the
    /// records are read while the folders are measured rather than after.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, ModOrganizerArchives>> OfAllAsync(
        IReadOnlyList<string> folders,
        CancellationToken ct)
    {
        if (folders.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, ModOrganizerArchives>>(
                new Dictionary<string, ModOrganizerArchives>(StringComparer.OrdinalIgnoreCase));
        }

        return WorkerThreads.Shared.RunAsync<IReadOnlyDictionary<string, ModOrganizerArchives>>(
            () => folders.ToDictionary(folder => folder, folder => Of(folder, ct), StringComparer.OrdinalIgnoreCase),
            ct);
    }

    /// <summary>
    /// Each archive in <paramref name="folder"/> classified by its <c>.meta</c>, as Mod Organizer 2's
    /// download list classifies it (<c>DownloadManager::DownloadInfo::createFromMeta</c>): a hidden download
    /// is left out of the list before anything else is read, a paused download is neither, then
    /// <c>uninstalled</c> wins over <c>installed</c>. An archive with no record, or a record
    /// that will not be read, is counted in neither, because Mod Organizer 2 lists it as ready to install.
    ///
    /// <para>The records are read up to <see cref="RecordsAtOnce"/> at a time (G4), and summed once every
    /// one is read, so the figures do not depend on the order the reads finish in. An archive whose record
    /// the listing does not show is not opened for one: the listing already names every record there is.</para>
    /// </summary>
    public static ModOrganizerArchives Of(string folder, CancellationToken ct)
    {
        if (FolderEntries.Of(folder) is not { } entries)
        {
            return new ModOrganizerArchives(0, 0, 0, Listed: false);
        }

        var names = entries
            .Where(entry => entry is not DirectoryInfo)
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        FileInfo[] recorded =
        [
            .. entries
                .OfType<FileInfo>()
                .Where(archive => !archive.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && !archive.Name.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase)
                    && !archive.Name.EndsWith(UnfinishedExtension, StringComparison.OrdinalIgnoreCase)
                    && names.Contains(archive.Name + MetaExtension)),
        ];

        var listings = new Listing[recorded.Length];

        // On threads of the read's own, because each read blocks its thread on the disk. See ListWorkers.
        ListWorkers.Run(recorded.Length, RecordsAtOnce, index => listings[index] = ListingOf(recorded[index]), ct);

        long installed = 0, uninstalled = 0, hidden = 0;

        for (var i = 0; i < recorded.Length; i++)
        {
            switch (listings[i])
            {
                case Listing.Installed:
                    installed += recorded[i].Length;
                    break;
                case Listing.Uninstalled:
                    uninstalled += recorded[i].Length;
                    break;
                case Listing.Hidden:
                    hidden += recorded[i].Length;
                    break;
            }
        }

        return new ModOrganizerArchives(installed, uninstalled, hidden, Listed: true);
    }

    private static Listing ListingOf(FileInfo archive)
    {
        if (QtIniFile.Read(archive.FullName + MetaExtension, MaximumMetaBytes) is not { } meta)
        {
            return Listing.Neither;
        }

        if (meta.IsTrue(QtIniFile.General, "removed"))
        {
            return Listing.Hidden;
        }

        if (meta.IsTrue(QtIniFile.General, "paused"))
        {
            return Listing.Neither;
        }

        if (meta.IsTrue(QtIniFile.General, "uninstalled"))
        {
            return Listing.Uninstalled;
        }

        return meta.IsTrue(QtIniFile.General, "installed") ? Listing.Installed : Listing.Neither;
    }
}
