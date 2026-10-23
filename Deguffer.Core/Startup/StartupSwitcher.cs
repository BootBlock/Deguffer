using System.Security;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Startup;

/// <summary>What one change did, and the §5.6 check after it.</summary>
/// <param name="Change">The change that was attempted, or null where the entry was refused before anything was written.</param>
/// <param name="Changed">Whether the value now reads back as the one written.</param>
public sealed record StartupChangeReport(
    StartupEntry Entry,
    StartupChange? Change,
    bool Changed,
    string Message,
    VerificationResult Verification)
{
    /// <summary>Whether the change was made and the §5.6 check passed.</summary>
    public bool IsComplete => Changed && Verification.Passed;

    public string Summary => Verification.Checks.Count == 0 ? Message : $"{Message} {Verification.Summary}";

    /// <summary>One line for each §5.6 check that did not pass.</summary>
    public IReadOnlyList<string> Details => [.. Verification.Unpassed.Select(c => $"{c.Subject}: {c.Detail}")];
}

/// <summary>
/// Stops an entry starting at sign-in, or lets it start again (§7.5): the entry and its value read
/// again and decided again, one <c>StartupApproved</c> value written, and the three exact checks of
/// §5.6 after it.
/// </summary>
public sealed class StartupSwitcher(IStartupRegistry registry, StartupFolderReader folders, TimeProvider time)
{
    public StartupChangeReport Switch(StartupEntry chosen, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(chosen);

        var verdict = StartupChangePolicy.MayChange(chosen, isElevated);

        if (verdict.Change is not { } change)
        {
            return NotChanged(chosen, null, verdict.Reason);
        }

        var source = chosen.Key.Source;
        var name = chosen.Key.Name;

        // Taken before anything is written, so the check afterwards compares against the machine the
        // user chose from rather than one this change already altered.
        var before = registry.ReadApprovals(source);

        if (before.Presence is PathPresence.Refused)
        {
            return NotChanged(chosen, change, "Windows would not open its record of whether this entry starts, so Deguffer changed nothing.");
        }

        if (!Same(before.Find(name), chosen.Approval?.Value))
        {
            return NotChanged(chosen, change, "Whether this entry starts changed after the list was read, so Deguffer left it. Refresh the list to see it as it is now.");
        }

        if (EntryNow(chosen) is { } refusal)
        {
            return NotChanged(chosen, change, refusal);
        }

        var value = change is StartupChange.Disable ? Approval.DisabledOn(time.GetUtcNow()) : Approval.Starting();
        string message;
        var changed = false;

        try
        {
            registry.WriteApproval(source, name, value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new StartupChangeReport(chosen, change, false, $"Windows refused the change: {ex.Message}", Verify(chosen, before, null));
        }

        var after = registry.ReadApprovals(source);

        if (after.Find(name) is { } written && written.SameAs(value))
        {
            changed = true;
            message = change is StartupChange.Disable
                ? $"'{chosen.Title}' will not start at sign-in. The entry itself is unchanged, and Enable lets it start again."
                : $"'{chosen.Title}' will start at sign-in again.";
        }
        else
        {
            message = "Windows accepted the change, but the value does not read back as the one Deguffer wrote.";
        }

        return new StartupChangeReport(chosen, change, changed, message, Verify(chosen, before, changed ? value : null, after));
    }

    /// <summary>
    /// Why the entry may not be changed now, or null where it is still the one the user chose: there,
    /// and holding the command or the file it held when the list was read.
    /// </summary>
    private string? EntryNow(StartupEntry chosen)
    {
        switch (chosen.Key.Source)
        {
            case StartupSource.UserRun or StartupSource.MachineRun or StartupSource.MachineRun32:
            {
                var run = registry.ReadRun(chosen.Key.Source);

                if (run.Presence is PathPresence.Refused)
                {
                    return "Windows would not open the entry's key to check it, so Deguffer changed nothing.";
                }

                return run.Find(chosen.Key.Name) is not { } now ? "The entry is gone, so Deguffer changed nothing."
                    : !now.SameAs(chosen.RunValue) ? "The entry changed after the list was read, so Deguffer left it. Refresh the list to see it as it is now."
                    : null;
            }

            default:
            {
                var (presence, stamp) = folders.Stamp(chosen.Key.Source, chosen.Key.Name);

                return presence switch
                {
                    PathPresence.Absent => "The entry is gone, so Deguffer changed nothing.",
                    PathPresence.Present when stamp == chosen.FileStamp => null,
                    PathPresence.Present => "The entry changed after the list was read, so Deguffer left it. Refresh the list to see it as it is now.",
                    _ => "Windows would not describe the entry's file to check it, so Deguffer changed nothing.",
                };
            }
        }
    }

    /// <summary>
    /// §5.6 for one value: what was written reads back as written, the entry is as it was, and every
    /// other value beside it is as it was. Each is exact, and each is one check, so the summary counts
    /// what was asserted.
    /// </summary>
    /// <param name="written">The value written, where it read back as written, or null where nothing was.</param>
    /// <param name="after">The <c>StartupApproved</c> key read after the write, or null to read it here.</param>
    private VerificationResult Verify(StartupEntry chosen, KeyValues before, StoredValue? written, KeyValues? after = null)
    {
        var source = chosen.Key.Source;
        var approvals = source.ApprovalLocation();
        var checks = new List<VerificationCheck>();

        if (written is not null)
        {
            checks.Add(new VerificationCheck(
                $@"{approvals}\{chosen.Key.Name}",
                "The one value Deguffer wrote",
                VerificationOutcome.Sent,
                "Reads back as written."));
        }

        checks.Add(EntryCheck(chosen));

        after ??= registry.ReadApprovals(source);

        foreach (var (name, value) in before.Values)
        {
            if (string.Equals(name, chosen.Key.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var subject = $@"{approvals}\{name}";
            const string reason = "A value beside the one Deguffer wrote";

            checks.Add(after.Presence is not PathPresence.Present
                ? new VerificationCheck(subject, reason, VerificationOutcome.Unverified, "Windows would not open the key to check.")
                : after.Find(name) is not { } now ? new VerificationCheck(subject, reason, VerificationOutcome.Failed, "It is gone.")
                : now.SameAs(value) ? new VerificationCheck(subject, reason, VerificationOutcome.Survived, "Unchanged.")
                : new VerificationCheck(subject, reason, VerificationOutcome.Failed, "It changed."));
        }

        return new VerificationResult { Checks = checks };
    }

    /// <summary>The entry itself, read again: §7.5 never writes it, so any difference is the alarm.</summary>
    private VerificationCheck EntryCheck(StartupEntry chosen)
    {
        var source = chosen.Key.Source;
        const string reason = "The entry itself";

        if (source is StartupSource.UserRun or StartupSource.MachineRun or StartupSource.MachineRun32)
        {
            var subject = $@"{source.Location(null)}\{chosen.Key.Name}";
            var run = registry.ReadRun(source);

            return run.Presence is not PathPresence.Present
                ? new VerificationCheck(subject, reason, VerificationOutcome.Unverified, "Windows would not open the key to check.")
                : run.Find(chosen.Key.Name) is not { } now ? new VerificationCheck(subject, reason, VerificationOutcome.Failed, "It is gone.")
                : now.SameAs(chosen.RunValue) ? new VerificationCheck(subject, reason, VerificationOutcome.Survived, "Unchanged.")
                : new VerificationCheck(subject, reason, VerificationOutcome.Failed, "It changed.");
        }

        var path = Path.Combine(folders.FolderOf(source) ?? string.Empty, chosen.Key.Name);
        var (presence, stamp) = folders.Stamp(source, chosen.Key.Name);

        return presence switch
        {
            PathPresence.Present when stamp == chosen.FileStamp => new VerificationCheck(path, reason, VerificationOutcome.Survived, "Unchanged."),
            PathPresence.Present => new VerificationCheck(path, reason, VerificationOutcome.Failed, "Its size or last write changed."),
            PathPresence.Absent => new VerificationCheck(path, reason, VerificationOutcome.Failed, "It is gone."),
            _ => new VerificationCheck(path, reason, VerificationOutcome.Unverified, "Windows would not describe it to check."),
        };
    }

    private static bool Same(StoredValue? now, StoredValue? then) => now is null ? then is null : now.SameAs(then);

    private static StartupChangeReport NotChanged(StartupEntry chosen, StartupChange? change, string reason) =>
        new(chosen, change, false, reason, new VerificationResult());
}
