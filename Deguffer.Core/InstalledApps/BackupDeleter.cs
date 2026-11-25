using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Text;

namespace Deguffer.Core.InstalledApps;

/// <summary>What happened to one backup the user chose to delete.</summary>
public sealed record BackupDeletionOutcome(RegistryBackupFile Backup, bool Deleted, string Message);

/// <summary>What one deletion of backups did, and the §5.6 check after it.</summary>
public sealed record BackupDeletionReport(IReadOnlyList<BackupDeletionOutcome> Items, VerificationResult Verification)
{
    public IReadOnlyList<BackupDeletionOutcome> Deleted => [.. Items.Where(i => i.Deleted)];

    public IReadOnlyList<BackupDeletionOutcome> NotDeleted => [.. Items.Where(i => !i.Deleted)];

    /// <summary>Whether everything chosen went and the §5.6 check passed.</summary>
    public bool IsComplete => NotDeleted.Count == 0 && Verification.Passed;

    /// <summary>One line for each backup left, and for each §5.6 check that did not pass.</summary>
    public IReadOnlyList<string> Details =>
    [
        .. NotDeleted.Select(i => $"{i.Backup.Path}: {i.Message}"),
        .. Verification.Unpassed.Select(c => $"{c.Subject}: {c.Detail}"),
    ];

    public string Summary =>
        (Deleted.Count, NotDeleted.Count) switch
        {
            (var deleted, 0) => $"Deleted {Counted.Of(deleted, "backup", "backups")}. {Verification.Summary}",
            (0, var left) => $"Deleted nothing: {Counted.Of(left, "backup", "backups")} could not be deleted. {Verification.Summary}",
            var (deleted, left) =>
                $"Deleted {Counted.Of(deleted, "backup", "backups")}, and {Counted.Of(left, "backup", "backups")} could not be deleted. "
                + Verification.Summary,
        };
}

/// <summary>
/// Deletes the backups the user chose (§7.3), for good: each decided again from the folder as it
/// stands, deleted alone, and followed by the §5.6 check that the folder and every other file in it
/// survived.
///
/// <para>Only a file the listing offered can be chosen, and the listing offers only a file in the
/// backup folder that parses as a backup (<see cref="RegistryBackupListing"/>). Everything else in
/// the folder, an interrupted restore's copy included, is a sibling this never touches and always
/// checks.</para>
/// </summary>
/// <param name="folder">Where backups are kept, in display form: <see cref="RegistryBackups.Folder"/>.</param>
/// <param name="fileSystem">
/// What deletes each file, through <see cref="FileRemover"/>: <see cref="WindowsFileSystem.Default"/>,
/// or a recorder that shows the form of the path that crosses into Win32 (§6.3).
/// </param>
public sealed class BackupDeleter(string folder, IFileSystem fileSystem)
{
    /// <summary>
    /// Whether <paramref name="backup"/> is a file this may delete at all: one directly inside the
    /// backup folder, named as a backup is. Decided from the path alone, before anything is read.
    /// </summary>
    public ActionVerdict MayDelete(RegistryBackupFile backup)
    {
        var name = Path.GetFileName(backup.Path);

        if (!string.Equals(Path.GetDirectoryName(backup.Path), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".reg", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith('.'))
        {
            return ActionVerdict.Refuse($"This file is not a backup in {folder}, so Deguffer will not delete it.");
        }

        return ActionVerdict.Allow($"Deletes {backup.Path}.");
    }

    public async Task<BackupDeletionReport> DeleteAsync(IReadOnlyList<RegistryBackupFile> chosen, CancellationToken ct)
    {
        // Every file in the folder, backup or not, read before anything is deleted, so the check
        // afterwards compares against the folder the user chose from.
        var before = FilesInFolder();
        var outcomes = new List<BackupDeletionOutcome>(chosen.Count);

        foreach (var backup in chosen)
        {
            outcomes.Add(ct.IsCancellationRequested
                ? new BackupDeletionOutcome(backup, false, "Not reached: the deletion was cancelled.")
                : await DeleteOneAsync(backup).ConfigureAwait(false));
        }

        var chosenPaths = chosen.Select(b => b.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedPaths = outcomes.Where(o => o.Deleted).Select(o => o.Backup.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new BackupDeletionReport(outcomes, Verify(before, deletedPaths, chosenPaths));
    }

    private async Task<BackupDeletionOutcome> DeleteOneAsync(RegistryBackupFile backup)
    {
        var verdict = MayDelete(backup);

        if (!verdict.IsAllowed)
        {
            return new BackupDeletionOutcome(backup, false, verdict.Reason);
        }

        switch (LongPath.ProbeFile(backup.Path))
        {
            case PathPresence.Absent:
                return new BackupDeletionOutcome(backup, false, "The file was already gone, so Deguffer deleted nothing.");
            case PathPresence.Refused:
                return new BackupDeletionOutcome(backup, false, "Windows would not describe the file, so Deguffer left it.");
        }

        // Decided again from the file as it stands: the list may be minutes old, and a file written
        // since is not the backup the user chose.
        if (new FileInfo(LongPath.Extended(backup.Path)).LastWriteTimeUtc != backup.Written.UtcDateTime)
        {
            return new BackupDeletionOutcome(backup, false,
                "The file changed after it was listed, so Deguffer left it. Refresh the list to see it as it is now.");
        }

        // Not cancellable once decided: one file either goes or stays, and the report says which.
        var removal = await FileRemover.RemoveAsync(backup.Path, fileSystem: fileSystem).ConfigureAwait(false);

        if (!removal.Removed)
        {
            return new BackupDeletionOutcome(backup, false,
                removal.Refused.IsEmpty ? "Something that is not a file has taken its name, so Deguffer left it."
                : removal.Refused.InUse.Files > 0 ? "Another program has the file open, so Windows would not delete it."
                : "Windows refused to delete the file.");
        }

        if (!removal.Took)
        {
            return new BackupDeletionOutcome(backup, false, "The file went before Deguffer deleted it.");
        }

        // Read back rather than trusted: the report says what the folder shows.
        return LongPath.ProbeFile(backup.Path) is PathPresence.Absent
            ? new BackupDeletionOutcome(backup, true, "Deleted.")
            : new BackupDeletionOutcome(backup, false, "Windows accepted the deletion, but the file is still there.");
    }

    /// <summary>Every file directly in the folder, in display form, or none where it cannot be listed.</summary>
    private List<string> FilesInFolder()
    {
        if (LongPath.ProbeDirectory(folder) is not PathPresence.Present)
        {
            return [];
        }

        try
        {
            return [.. new DirectoryInfo(LongPath.Extended(folder)).EnumerateFiles().Select(f => LongPath.Display(f.FullName))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that stops listing part-way leaves nothing to compare against; the check
            // still asserts that the folder itself stands.
            return [];
        }
    }

    /// <summary>§5.6: the backup folder still stands, and every file in it beside the deleted ones is still there.</summary>
    private VerificationResult Verify(List<string> before, IReadOnlySet<string> deleted, IReadOnlySet<string> chosen)
    {
        var checks = new List<VerificationCheck>
        {
            LongPath.ProbeDirectory(folder) switch
            {
                PathPresence.Present => new VerificationCheck(folder, "The backup folder itself", VerificationOutcome.Survived, "Still there."),
                PathPresence.Refused => new VerificationCheck(folder, "The backup folder itself", VerificationOutcome.Unverified,
                    "Windows would not describe it to check."),
                _ => new VerificationCheck(folder, "The backup folder itself", VerificationOutcome.Failed, "It is gone."),
            },
        };

        foreach (var path in before.Where(p => !deleted.Contains(p)))
        {
            checks.Add(LongPath.ProbeFile(path) switch
            {
                PathPresence.Present => new VerificationCheck(path, "A file beside the deleted backups", VerificationOutcome.Survived, "Still there."),
                PathPresence.Refused => new VerificationCheck(path, "A file beside the deleted backups", VerificationOutcome.Unverified,
                    "Windows would not describe it to check."),

                // A chosen backup Deguffer left that is gone went some other way; anything else
                // missing is the alarm §5.6 exists to raise.
                _ when chosen.Contains(path) => new VerificationCheck(path, "A chosen backup Deguffer did not delete",
                    VerificationOutcome.RemovedFromOutside, "It is gone, and Deguffer did not delete it."),
                _ => new VerificationCheck(path, "A file beside the deleted backups", VerificationOutcome.Failed, "It is gone."),
            });
        }

        return new VerificationResult { Checks = checks };
    }
}
