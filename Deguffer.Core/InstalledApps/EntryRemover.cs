using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>What happened to one entry the user chose.</summary>
/// <param name="BackupPath">The backup written before the removal, or null where none was.</param>
public sealed record EntryRemovalOutcome(InstalledEntry Entry, bool Removed, string Message, string? BackupPath = null);

/// <summary>What one removal did, and the §5.6 check after it.</summary>
public sealed record EntryRemovalReport(IReadOnlyList<EntryRemovalOutcome> Items, VerificationResult Verification)
{
    public IReadOnlyList<EntryRemovalOutcome> Removed => [.. Items.Where(i => i.Removed)];

    public IReadOnlyList<EntryRemovalOutcome> NotRemoved => [.. Items.Where(i => !i.Removed)];

    /// <summary>Whether everything chosen went and the §5.6 check passed.</summary>
    public bool IsComplete => NotRemoved.Count == 0 && Verification.Passed;

    /// <summary>One line for each entry left, and for each §5.6 check that did not pass.</summary>
    public IReadOnlyList<string> Details =>
    [
        .. NotRemoved.Select(i => $"{i.Entry.Name}: {i.Message}"),
        .. Verification.Checks
            .Where(c => c.Outcome is not (VerificationOutcome.Survived or VerificationOutcome.NotPresentBefore))
            .Select(c => $"{c.Subject}: {c.Detail}"),
    ];

    public string Summary =>
        (Removed.Count, NotRemoved.Count) switch
        {
            (var removed, 0) => $"Removed {Plural(removed)}. {Verification.Summary}",
            (0, var refused) => $"Removed nothing: {Plural(refused)} could not be removed. {Verification.Summary}",
            var (removed, refused) => $"Removed {Plural(removed)}, and {Plural(refused)} could not be removed. {Verification.Summary}",
        };

    private static string Plural(int count) => count == 1 ? "1 entry" : $"{count} entries";
}

/// <summary>
/// Removes the entries the user chose (§7.3): each read and decided again, backed up first when
/// asked, deleted alone, and followed by the §5.6 check that everything beside it survived.
/// </summary>
public sealed class EntryRemover(IUninstallRegistry registry, IWindowsInstaller installer, RegistryBackups backups)
{
    public async Task<EntryRemovalReport> RemoveAsync(
        IReadOnlyList<InstalledEntry> chosen,
        bool backUp,
        bool isElevated,
        CancellationToken ct)
    {
        var reader = new InstalledAppsReader(registry, installer);
        var chosenKeys = chosen.Select(e => e.Key).ToHashSet();

        // Taken before anything is deleted, so the check afterwards compares against the machine
        // the user chose from rather than one this removal already changed.
        var before = chosen.Select(e => e.Key.Scope).Distinct().ToDictionary(scope => scope, registry.Read);

        var outcomes = new List<EntryRemovalOutcome>(chosen.Count);

        foreach (var entry in chosen)
        {
            if (ct.IsCancellationRequested)
            {
                outcomes.Add(new EntryRemovalOutcome(entry, false, "Not reached: the removal was cancelled."));
                continue;
            }

            outcomes.Add(await RemoveOneAsync(entry, reader, backUp, isElevated, ct).ConfigureAwait(false));
        }

        var removedKeys = outcomes.Where(o => o.Removed).Select(o => o.Entry.Key).ToHashSet();

        return new EntryRemovalReport(outcomes, Verify(before, removedKeys, chosenKeys));
    }

    private async Task<EntryRemovalOutcome> RemoveOneAsync(
        InstalledEntry chosen,
        InstalledAppsReader reader,
        bool backUp,
        bool isElevated,
        CancellationToken ct)
    {
        // Decided again from the machine as it now stands: the list the user chose from may be
        // minutes old, and an installer may have run since.
        var now = reader.ReadAgain(chosen.Key);

        if (now is null)
        {
            return new EntryRemovalOutcome(chosen, false, "The entry was already gone, so Deguffer removed nothing.");
        }

        if (!now.Values.SameAs(chosen.Values))
        {
            return new EntryRemovalOutcome(chosen, false, "The entry changed after it was chosen, so Deguffer left it. Refresh the list to see it as it is now.");
        }

        var verdict = EntryRemovalPolicy.MayRemove(now, isElevated);

        if (!verdict.IsAllowed)
        {
            return new EntryRemovalOutcome(chosen, false, verdict.Reason);
        }

        string? backupPath = null;

        if (backUp)
        {
            try
            {
                var backup = await backups.ExportAsync(now.Key, ct).ConfigureAwait(false);

                if (!backup.Succeeded)
                {
                    return new EntryRemovalOutcome(chosen, false, $"{backup.Message} The entry was not removed.");
                }

                backupPath = backup.Path;
            }
            catch (OperationCanceledException)
            {
                return new EntryRemovalOutcome(chosen, false, "Not removed: the removal was cancelled during its backup.");
            }
        }

        try
        {
            registry.Delete(now.Key);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new EntryRemovalOutcome(chosen, false, $"Windows refused to remove the entry: {ex.Message}", backupPath);
        }

        // Read back rather than trusted: the report says what the machine shows.
        return registry.ReadOne(now.Key).Presence is PathPresence.Absent
            ? new EntryRemovalOutcome(chosen, true, "Removed from the list.", backupPath)
            : new EntryRemovalOutcome(chosen, false, "Windows accepted the removal, but the entry is still there.", backupPath);
    }

    /// <summary>
    /// §5.6: each <c>Uninstall</c> key still stands, and every entry beside the removed ones that was
    /// there before is there now.
    /// </summary>
    private VerificationResult Verify(
        IReadOnlyDictionary<UninstallScope, UninstallRead> before,
        IReadOnlySet<UninstallKey> removed,
        IReadOnlySet<UninstallKey> chosen)
    {
        var checks = new List<VerificationCheck>();

        foreach (var (scope, read) in before)
        {
            var after = registry.Read(scope);
            var parent = scope.PhysicalPath();

            checks.Add(after.Presence switch
            {
                PathPresence.Present => new VerificationCheck(parent, "The Uninstall key itself", VerificationOutcome.Survived, "Still there."),
                PathPresence.Refused => new VerificationCheck(parent, "The Uninstall key itself", VerificationOutcome.Unverified,
                    "Windows would not open it to check."),
                _ => new VerificationCheck(parent, "The Uninstall key itself", VerificationOutcome.Failed, "It is gone."),
            });

            if (after.Presence is not PathPresence.Present)
            {
                continue;
            }

            var standing = after.Records.Select(r => r.Key).ToHashSet();
            var expected = read.Records.Select(r => r.Key).Where(k => !removed.Contains(k)).ToList();

            foreach (var key in expected)
            {
                // A chosen entry that was not removed and is gone went some other way; anything
                // else missing is the alarm §5.6 exists to raise. One check per entry, so the
                // summary counts what was asserted.
                checks.Add(standing.Contains(key)
                    ? new VerificationCheck(key.PhysicalPath, "An entry beside the removed ones", VerificationOutcome.Survived, "Still there.")
                    : chosen.Contains(key)
                        ? new VerificationCheck(key.PhysicalPath, "A chosen entry Deguffer did not remove", VerificationOutcome.RemovedFromOutside,
                            "It is gone, and Deguffer did not delete it.")
                        : new VerificationCheck(key.PhysicalPath, "An entry beside the removed ones", VerificationOutcome.Failed, "It is gone."));
            }
        }

        return new VerificationResult { Checks = checks };
    }
}
