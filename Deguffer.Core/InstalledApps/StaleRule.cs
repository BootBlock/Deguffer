using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>What the stale rule asks the machine, each question behind a seam.</summary>
/// <param name="NonInstallerEntryNamed">
/// Whether an <c>Uninstall</c> entry that is not a Windows Installer product's is named by a code,
/// in any scope: how a code that names another bundle is told apart from a product code.
/// </param>
public sealed record StaleEvidence(
    IWindowsInstaller Installer,
    InstalledPaths Paths,
    IPackageDependencies Dependencies,
    Func<Guid, PathPresence> NonInstallerEntryNamed);

/// <summary>Either a finished verdict, or the evidence that what the entry describes is gone.</summary>
internal readonly record struct Gone(StandingVerdict? Verdict, string? Evidence)
{
    public static Gone Proven(string evidence) => new(null, evidence);

    public static Gone Unproven(string reason) => new(new StandingVerdict(EntryStanding.Unproven, reason), null);

    public static Gone Installed(string reason) => new(new StandingVerdict(EntryStanding.Installed, reason), null);
}

/// <summary>
/// The rule that decides <see cref="EntryStanding"/>: stale only on proof, and a refusal is never
/// proof (§7.3).
/// </summary>
public static class StaleRule
{
    public static StandingVerdict Decide(UninstallRecord record, UninstallCommand command, StaleEvidence evidence)
    {
        if (!record.IsReadable)
        {
            return new StandingVerdict(EntryStanding.Unproven,
                "Deguffer could not read this entry, so it cannot tell whether the program is still there.");
        }

        var gone = ProductCodeOf(record, command) is { } code ? AskInstaller(evidence.Installer.QueryProductState(code))
            : BundleRule.IsBundle(record, evidence.Dependencies.Read()) ? WithBundleUninstaller(BundleRule.Judge(record, evidence), command, evidence.Paths)
            : AskUninstaller(command, evidence.Paths);

        return gone.Verdict ?? WithInstallLocation(gone.Evidence!, InstallLocation.Of(record.Values), evidence.Paths);
    }

    /// <summary>
    /// The product code Windows Installer answers for: the key's own name where the entry says it is
    /// a Windows Installer entry, or the code its <c>MsiExec.exe</c> command names. A key named like
    /// a product code proves nothing by itself: 45 such entries on the measured machine were not
    /// Windows Installer products.
    /// </summary>
    public static Guid? ProductCodeOf(UninstallRecord record, UninstallCommand command) =>
        IsInstallerEntry(record.Key.Name, record.Values, out var fromKey) ? fromKey
        : command is InstallerCommand installer ? installer.ProductCode
        : null;

    /// <summary>Whether an entry is a Windows Installer product's own, named by its product code.</summary>
    public static bool IsInstallerEntry(string name, UninstallValues values, out Guid code) =>
        Guid.TryParseExact(name, "B", out code) && values.Flag("WindowsInstaller");

    private static Gone AskInstaller(InstallerProductState state) => state switch
    {
        InstallerProductState.Installed => Gone.Installed("Windows Installer reports this product installed."),
        InstallerProductState.Advertised => Gone.Installed(
            "Windows Installer reports this product advertised, ready to install on first use."),
        InstallerProductState.OtherAccount => new(new StandingVerdict(EntryStanding.OtherAccount,
            "Windows Installer reports this product installed for another account."), null),
        InstallerProductState.Unknown => Gone.Proven("Windows Installer does not know this product"),
        _ => Gone.Unproven(
            "Windows Installer did not answer for this product, so Deguffer cannot tell whether it is still there."),
    };

    private static Gone AskUninstaller(UninstallCommand command, InstalledPaths paths) => command switch
    {
        ProgramCommand program => AskProgram(program, paths),
        NamedCommand named => Gone.Unproven(
            $"The uninstall command runs {named.Name}, which says nothing about whether the program is still there."),
        UnparsedCommand => Gone.Unproven("Deguffer cannot read an uninstaller from this entry's uninstall command."),
        _ => Gone.Unproven("This entry has no uninstall command, so there is no uninstaller to look for."),
    };

    private static Gone AskProgram(ProgramCommand program, InstalledPaths paths) => program.Presence switch
    {
        PathPresence.Present => Gone.Installed($"The uninstaller {program.Executable} is there."),
        PathPresence.Absent => paths.WhyAbsenceProvesNothing(program.Executable) is { } why
            ? Gone.Unproven(why)
            : Gone.Proven($"The uninstaller {program.Executable} is gone"),
        _ => Gone.Unproven($"Windows would not say whether the uninstaller {program.Executable} is there."),
    };

    /// <summary>
    /// A bundle whose packages are gone, and whose command names an uninstaller, needs that
    /// uninstaller gone too: a bundle that can still run its own uninstaller is not one Windows
    /// cannot remove.
    /// </summary>
    private static Gone WithBundleUninstaller(Gone packages, UninstallCommand command, InstalledPaths paths)
    {
        if (packages.Verdict is not null || command is not ProgramCommand program)
        {
            return packages;
        }

        var uninstaller = AskProgram(program, paths);

        return uninstaller.Verdict is not null
            ? uninstaller
            : Gone.Proven($"{packages.Evidence}, and the uninstaller {program.Executable} is gone");
    }

    private static StandingVerdict WithInstallLocation(string evidence, InstallLocation location, InstalledPaths paths)
    {
        switch (location.Kind)
        {
            case InstallLocationKind.NotSet:
                return new StandingVerdict(EntryStanding.Stale, $"{evidence}, and the entry names no install folder.");

            case InstallLocationKind.Uncheckable:
                return new StandingVerdict(EntryStanding.Unproven, location.Text.Length > 0
                    ? $"{evidence}, but the entry's install folder \"{location.Text}\" is not a full path, so Deguffer cannot check it."
                    : $"{evidence}, but the entry's install folder is not written as text, so Deguffer cannot check it.");
        }

        var path = location.Text;

        // Asked as either kind: some installers name a file here, and a file standing there is the
        // program standing there.
        return paths.ProbeEntry(path) switch
        {
            PathPresence.Absent when paths.WhyAbsenceProvesNothing(path) is { } why =>
                new StandingVerdict(EntryStanding.Unproven, $"{evidence}, but {why}"),
            PathPresence.Absent => new StandingVerdict(EntryStanding.Stale,
                $"{evidence}, and the install folder {path} is gone too."),
            PathPresence.Present => new StandingVerdict(EntryStanding.Installed,
                $"{evidence}, but the install folder {path} is still there, so the install may be broken rather than gone."),
            _ => new StandingVerdict(EntryStanding.Unproven,
                $"{evidence}, but Windows would not say whether the install folder {path} is there."),
        };
    }
}
