namespace Deguffer.Core.InstalledApps;

/// <summary>
/// What the user is asked before an Installed apps action, and what they are told it does (§7.3).
///
/// <para>In Core because the confirmation is not a preference, and a sentence that exists only
/// inside a dialog is a sentence nothing can hold Deguffer to.</para>
/// </summary>
/// <param name="Title">The question, naming the subject.</param>
/// <param name="Consequence">What happens, and what does not.</param>
/// <param name="Items">The entries or the command concerned, one line each.</param>
/// <param name="ConfirmLabel">The affirmative button.</param>
public sealed record InstalledAppsPrompt(string Title, string Consequence, IReadOnlyList<string> Items, string ConfirmLabel)
{
    public static InstalledAppsPrompt ForRemoval(IReadOnlyList<InstalledEntry> entries, bool backUp, string backupFolder)
    {
        var subject = entries is [{ } only] ? $"'{only.Name}'" : $"{entries.Count} entries";

        var backup = backUp
            ? $"Each entry is backed up to {backupFolder} first, and an entry whose backup fails is not removed. "
              + "You can restore a backup from this page."
            : "No backup is taken, so the removal cannot be undone from Deguffer.";

        return new InstalledAppsPrompt(
            $"Remove {subject} from the installed apps list?",
            "Deguffer deletes each entry's registry key. Nothing is uninstalled and no file is touched. " + backup,
            [.. entries.Select(e => $"{e.Name} ({e.Key.Scope.Describe()})")],
            entries.Count == 1 ? "Remove entry" : "Remove entries");
    }

    public static InstalledAppsPrompt ForRestore(RegistryBackupFile backup) => new(
        $"Restore '{backup.DisplayName ?? backup.KeyPath}'?",
        "Deguffer imports the backup with reg.exe, which writes the entry back as the file records it. "
        + "The file is kept afterwards.",
        [backup.KeyPath],
        "Restore");

    public static InstalledAppsPrompt ForUninstall(InstalledEntry entry, UninstallLaunch launch) => new(
        $"Uninstall '{entry.Name}'?",
        "Deguffer runs the program's own uninstaller, which may ask you questions and may ask for administrator "
        + "rights. What it removes is up to the uninstaller. When it exits, Deguffer reads the entry again and "
        + "reports what the list now says.",
        [launch.Display],
        "Uninstall");
}

/// <summary>
/// Puts an Installed apps action to the user. What is asked comes from
/// <see cref="InstalledAppsPrompt"/>; this seam only carries it to a surface that can ask.
/// </summary>
public interface IInstalledAppsConfirmation
{
    /// <summary>Whether the user said yes. Declining is a decision, not a failure.</summary>
    Task<bool> AskAsync(InstalledAppsPrompt prompt, CancellationToken ct);
}
