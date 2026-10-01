namespace Deguffer.Core.InstalledApps;

/// <summary>What the list says about a program after its uninstaller exited.</summary>
public enum AfterUninstall
{
    /// <summary>The uninstaller did not run, so nothing was asked of the list.</summary>
    NotRun,

    /// <summary>The entry is gone.</summary>
    EntryGone,

    /// <summary>The entry is still there and the program is still installed.</summary>
    StillInstalled,

    /// <summary>The entry is still there, and the machine now proves it stale.</summary>
    NowStale,
}

/// <summary>What one uninstall did, and what the list says afterwards.</summary>
/// <param name="After">
/// What the entry says now. The §5.6 evidence this action can honestly give: what the uninstaller
/// removed is the uninstaller's business, so Deguffer reports the list rather than a claim.
/// </param>
public sealed record UninstallReport(InstalledEntry Entry, AfterUninstall After, string Message)
{
    public string Summary => After switch
    {
        AfterUninstall.NotRun => Message,
        AfterUninstall.EntryGone => $"{Message} '{Entry.Name}' is no longer in the list.",
        AfterUninstall.NowStale => $"{Message} The entry for '{Entry.Name}' is still in the list, but the program is gone, so the entry can now be removed.",
        _ => $"{Message} '{Entry.Name}' is still in the list. Some uninstallers finish after they exit, so refresh the list later to see where it stands.",
    };
}

/// <summary>
/// Runs one installed program's own uninstaller (§7.3): decided again from the machine as it
/// stands, watched with no deadline, and followed by a fresh read of the entry.
/// </summary>
public sealed class ProgramUninstaller(InstalledAppsReader reader, IUninstallLauncher launcher, string msiexec)
{
    /// <summary>
    /// What <paramref name="entry"/> would run as it was read, with no fresh read. For the page's
    /// first decision, made whenever the selection changes; <see cref="Prepare"/> makes the second.
    /// </summary>
    public UninstallJudgement Judge(InstalledEntry entry)
    {
        var (verdict, launch) = UninstallPolicy.MayUninstall(entry, msiexec);

        return new UninstallJudgement(verdict, launch is null ? null : new PreparedUninstall(entry, launch));
    }

    /// <summary>
    /// The launch <paramref name="chosen"/> would run now, for the confirmation, or the refusal. Read
    /// again so the command confirmed is the command that runs.
    /// </summary>
    public UninstallJudgement Prepare(InstalledEntry chosen) =>
        reader.ReadAgain(chosen.Key) is { } current
            ? Judge(current)
            : new UninstallJudgement(ActionVerdict.Refuse("The entry is already gone."), null);

    public async Task<UninstallReport> UninstallAsync(PreparedUninstall confirmed, CancellationToken ct)
    {
        var (verdict, prepared) = Prepare(confirmed.Entry);

        if (prepared is not { Entry: var current, Launch: var launch })
        {
            return new UninstallReport(confirmed.Entry, AfterUninstall.NotRun, verdict.Reason);
        }

        if (launch != confirmed.Launch)
        {
            return new UninstallReport(confirmed.Entry, AfterUninstall.NotRun,
                "The entry's uninstall command changed after you confirmed it, so Deguffer did not run it.");
        }

        // The last moment a cancel can still keep the uninstaller from starting: the launcher sees
        // the token only once the process is running.
        if (ct.IsCancellationRequested)
        {
            return new UninstallReport(confirmed.Entry, AfterUninstall.NotRun,
                $"The uninstaller of '{confirmed.Entry.Name}' was cancelled before it started, so Deguffer did not run it.");
        }

        var outcome = await launcher.RunAsync(launch, ct).ConfigureAwait(false);

        if (!outcome.Started)
        {
            return new UninstallReport(current, AfterUninstall.NotRun, outcome.Message);
        }

        var after = reader.ReadAgain(current.Key);

        return new UninstallReport(
            current,
            after is null ? AfterUninstall.EntryGone
                : after.IsStale ? AfterUninstall.NowStale
                : AfterUninstall.StillInstalled,
            outcome.Message);
    }
}
