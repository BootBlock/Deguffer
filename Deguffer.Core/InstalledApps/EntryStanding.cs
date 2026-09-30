using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>Which list an entry belongs in (§7.3).</summary>
public enum EntryStanding
{
    /// <summary>The machine proves what the entry describes is gone. The only removable kind.</summary>
    Stale,

    /// <summary>The machine shows the program is there.</summary>
    Installed,

    /// <summary>
    /// Nothing proved the program there or gone. Listed with the installed entries, because short of
    /// proof an entry stays.
    /// </summary>
    Unproven,

    /// <summary>A Windows Installer product installed for another account. Refused both actions.</summary>
    OtherAccount,
}

/// <param name="Reason">What the evidence showed, in the user's words.</param>
public sealed record StandingVerdict(EntryStanding Standing, string Reason)
{
    public bool IsStale => Standing is EntryStanding.Stale;
}

/// <summary>
/// The rule that decides <see cref="EntryStanding"/>: stale only on proof, and a refusal is never
/// proof (§7.3).
/// </summary>
public static class StaleRule
{
    /// <param name="askInstaller">Windows Installer's answer for a product code.</param>
    /// <param name="probeDirectory">Whether a directory is there, keeping a refusal apart from absence.</param>
    public static StandingVerdict Decide(
        UninstallRecord record,
        UninstallCommand command,
        Func<Guid, InstallerProductState> askInstaller,
        Func<string, PathPresence> probeDirectory)
    {
        if (!record.IsReadable)
        {
            return new StandingVerdict(EntryStanding.Unproven,
                "Deguffer could not read this entry, so it cannot tell whether the program is still there.");
        }

        var gone = ProductCodeOf(record, command) is { } code
            ? AskInstaller(askInstaller(code))
            : AskUninstaller(command);

        return gone.Verdict ?? WithInstallFolder(gone.Evidence!, InstallLocationOf(record.Values), probeDirectory);
    }

    /// <summary>
    /// The product code Windows Installer answers for: the key's own name where the entry says it is
    /// a Windows Installer entry, or the code its <c>MsiExec.exe</c> command names. A key named like
    /// a product code proves nothing by itself: 45 such entries on the measured machine were not
    /// Windows Installer products.
    /// </summary>
    public static Guid? ProductCodeOf(UninstallRecord record, UninstallCommand command) =>
        record.Values.Flag("WindowsInstaller") && IsBracedGuid(record.Key.Name, out var fromKey) ? fromKey
        : command is InstallerCommand installer ? installer.ProductCode
        : null;

    /// <summary>
    /// The entry's install folder, or null where it names none or names something that is not a
    /// full path. Some installers write the value quoted, or with a trailing separator.
    /// </summary>
    public static string? InstallLocationOf(UninstallValues values)
    {
        var location = values.Text("InstallLocation")?.Trim('"').Trim();

        if (string.IsNullOrEmpty(location))
        {
            return null;
        }

        location = Path.TrimEndingDirectorySeparator(location);

        return Path.IsPathFullyQualified(location) ? location : null;
    }

    private static bool IsBracedGuid(string name, out Guid code) =>
        Guid.TryParseExact(name, "B", out code);

    /// <summary>Either a finished verdict, or the evidence that the uninstaller is gone.</summary>
    private readonly record struct Gone(StandingVerdict? Verdict, string? Evidence);

    private static Gone AskInstaller(InstallerProductState state) => state switch
    {
        InstallerProductState.Installed => new(new StandingVerdict(EntryStanding.Installed,
            "Windows Installer reports this product installed."), null),
        InstallerProductState.Advertised => new(new StandingVerdict(EntryStanding.Installed,
            "Windows Installer reports this product advertised, ready to install on first use."), null),
        InstallerProductState.OtherAccount => new(new StandingVerdict(EntryStanding.OtherAccount,
            "Windows Installer reports this product installed for another account."), null),
        InstallerProductState.Unknown => new(null, "Windows Installer does not know this product"),
        _ => new(new StandingVerdict(EntryStanding.Unproven,
            "Windows Installer did not answer for this product, so Deguffer cannot tell whether it is still there."), null),
    };

    private static Gone AskUninstaller(UninstallCommand command) => command switch
    {
        ProgramCommand { Presence: PathPresence.Present } program => new(new StandingVerdict(EntryStanding.Installed,
            $"The uninstaller {program.Executable} is there."), null),
        ProgramCommand { Presence: PathPresence.Absent } program => new(null,
            $"The uninstaller {program.Executable} is gone"),
        ProgramCommand program => new(new StandingVerdict(EntryStanding.Unproven,
            $"Windows would not say whether the uninstaller {program.Executable} is there."), null),
        NamedCommand named => new(new StandingVerdict(EntryStanding.Unproven,
            $"The uninstall command runs {named.Name}, which says nothing about whether the program is still there."), null),
        UnparsedCommand => new(new StandingVerdict(EntryStanding.Unproven,
            "Deguffer cannot read an uninstaller from this entry's uninstall command."), null),
        _ => new(new StandingVerdict(EntryStanding.Unproven,
            "This entry has no uninstall command, so there is no uninstaller to look for."), null),
    };

    private static StandingVerdict WithInstallFolder(
        string evidence,
        string? location,
        Func<string, PathPresence> probeDirectory)
    {
        if (location is null)
        {
            return new StandingVerdict(EntryStanding.Stale, $"{evidence}, and the entry names no install folder.");
        }

        return probeDirectory(location) switch
        {
            PathPresence.Absent => new StandingVerdict(EntryStanding.Stale,
                $"{evidence}, and the install folder {location} is gone too."),
            PathPresence.Present => new StandingVerdict(EntryStanding.Installed,
                $"{evidence}, but the install folder {location} is still there, so the install may be broken rather than gone."),
            _ => new StandingVerdict(EntryStanding.Unproven,
                $"{evidence}, but Windows would not say whether the install folder {location} is there."),
        };
    }
}
