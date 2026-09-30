using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>
/// Restores a backup §7.3 took: refused where the entry already exists, because an import merges
/// and a merge into a live entry is not a restore, and checked afterwards by reading the entry back.
/// </summary>
public sealed class BackupRestorer(IUninstallRegistry registry, RegistryBackups backups)
{
    /// <summary>Whether <paramref name="backup"/> may be restored now, decided before asking and again before importing.</summary>
    public ActionVerdict MayRestore(RegistryBackupFile backup, bool isElevated)
    {
        if (backup.Key is not { } key)
        {
            return ActionVerdict.Refuse("This file does not name an installed apps entry, so Deguffer will not import it.");
        }

        if (!backup.IsConfined)
        {
            return ActionVerdict.Refuse(
                "This file writes keys beyond its entry, or deletes something, so it is not a backup Deguffer will import.");
        }

        var presence = registry.ReadOne(key).Presence;

        if (presence is not PathPresence.Absent)
        {
            return ActionVerdict.Refuse(presence is PathPresence.Present
                ? "The entry is already in the list, and importing over it would merge the two."
                : "Windows would not say whether the entry is already there, so Deguffer will not import over it.");
        }

        if (key.Scope.NeedsAdministrator() && !isElevated)
        {
            return ActionVerdict.Refuse(
                "This entry is for all users, and restoring it needs administrator rights. Reopen Deguffer as administrator to restore it.");
        }

        return ActionVerdict.Allow($"Restores {backup.KeyPath}.");
    }

    public async Task<BackupOutcome> RestoreAsync(RegistryBackupFile backup, bool isElevated, CancellationToken ct)
    {
        var verdict = MayRestore(backup, isElevated);

        if (!verdict.IsAllowed)
        {
            return new BackupOutcome(false, backup.Path, verdict.Reason);
        }

        var imported = await backups.ImportAsync(backup, ct).ConfigureAwait(false);

        if (!imported.Succeeded)
        {
            return imported;
        }

        return registry.ReadOne(backup.Key!).Presence is PathPresence.Present
            ? new BackupOutcome(true, backup.Path, "Restored. The entry is back in the list.")
            : new BackupOutcome(false, backup.Path, "reg.exe reported success, but the entry is not there.");
    }
}
