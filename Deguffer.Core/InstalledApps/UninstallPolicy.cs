using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>What Deguffer starts to uninstall a program: a file, and the arguments it is given.</summary>
public sealed record UninstallLaunch(string FileName, string Arguments)
{
    /// <summary>The command as the confirmation shows it.</summary>
    public string Display => Arguments.Length == 0 ? $"\"{FileName}\"" : $"\"{FileName}\" {Arguments}";
}

/// <summary>
/// Which installed program may be uninstalled, and with what (§7.3). Decided when an entry is
/// selected and again by <see cref="ProgramUninstaller"/> immediately before the uninstaller starts.
/// </summary>
public static class UninstallPolicy
{
    /// <param name="msiexec">Windows Installer's own <c>msiexec.exe</c>, as <see cref="NativeSystemTool"/> names it.</param>
    /// <returns>The verdict, and the launch where it allows one.</returns>
    public static (ActionVerdict Verdict, UninstallLaunch? Launch) MayUninstall(InstalledEntry entry, string msiexec)
    {
        if (entry.Standing.Standing is EntryStanding.OtherAccount)
        {
            return Refuse("This program was installed for another account, and only that account can uninstall it.");
        }

        if (entry.IsStale)
        {
            return Refuse($"The program is already gone, so there is nothing to uninstall. Remove the entry instead. {entry.Standing.Reason}");
        }

        if (entry.NoRemove)
        {
            return Refuse("The entry says it cannot be uninstalled, and Windows offers no uninstall button for it either.");
        }

        // A Windows Installer product is removed by its code: /I opens a maintenance dialog, and
        // Windows itself drives such an entry through Windows Installer rather than its command.
        if (StaleRule.ProductCodeOf(new UninstallRecord(entry.Key, entry.Values), entry.Command) is { } code)
        {
            return Allow(new UninstallLaunch(msiexec, $"/x {code:B}"));
        }

        return entry.Command switch
        {
            ProgramCommand { Presence: PathPresence.Present } program => Allow(new UninstallLaunch(program.Executable, program.Arguments)),
            ProgramCommand { Presence: PathPresence.Absent } program => Refuse(
                $"The uninstaller {program.Executable} is not there, so it cannot be run. {entry.Standing.Reason}"),
            ProgramCommand program => Refuse(
                $"Windows would not say whether the uninstaller {program.Executable} is there, so Deguffer will not run it."),
            NamedCommand named => Allow(new UninstallLaunch(named.Name, named.Arguments)),
            UnparsedCommand => Refuse("Deguffer cannot read an uninstaller from this entry's uninstall command."),
            _ => Refuse("This entry has no uninstall command."),
        };

        static (ActionVerdict, UninstallLaunch?) Refuse(string reason) => (ActionVerdict.Refuse(reason), null);

        static (ActionVerdict, UninstallLaunch?) Allow(UninstallLaunch launch) =>
            (ActionVerdict.Allow($"Runs {launch.Display}."), launch);
    }
}
