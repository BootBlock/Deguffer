namespace Deguffer.Core.InstalledApps;

/// <summary>A program and the command that will uninstall it, as the user confirms them.</summary>
public sealed record PreparedUninstall(InstalledEntry Entry, UninstallLaunch Launch);

/// <summary>Whether a program may be uninstalled, and what would run where it may.</summary>
/// <param name="Prepared">The program and its launch, or null where the verdict refuses.</param>
public sealed record UninstallJudgement(ActionVerdict Verdict, PreparedUninstall? Prepared);

/// <summary>
/// What a selection of installed rows may uninstall, and what the page says about it (§7.3).
/// Built from <see cref="ProgramUninstaller.Judge"/> when the selection changes, and from
/// <see cref="ProgramUninstaller.Prepare"/> immediately before the confirmation, so the commands
/// confirmed are the commands the entries hold then.
/// </summary>
/// <param name="Runnable">The programs an uninstall would run, in the order selected.</param>
/// <param name="Note">What will run, and why the rest will be left. Empty where nothing is selected.</param>
public sealed record UninstallSelection(IReadOnlyList<PreparedUninstall> Runnable, string Note)
{
    public static UninstallSelection For(IReadOnlyList<InstalledEntry> selected, Func<InstalledEntry, UninstallJudgement> judge)
    {
        var judged = selected.Select(e => (Entry: e, Judgement: judge(e))).ToList();
        var runnable = judged.Select(j => j.Judgement.Prepared).OfType<PreparedUninstall>().ToList();
        var refused = judged.Where(j => j.Judgement.Prepared is null).ToList();

        var runs = runnable switch
        {
            [] => null,
            [_] when refused.Count == 0 => judged[0].Judgement.Verdict.Reason,
            [var only] => $"Runs the uninstaller of '{only.Entry.Name}'.",
            _ => $"Runs {runnable.Count} uninstallers, one at a time, and asks before starting each one after the first.",
        };

        var left = refused switch
        {
            [] => null,
            [var only] when runnable.Count == 0 => only.Judgement.Verdict.Reason,
            [var only] => $"'{only.Entry.Name}' will be left. {only.Judgement.Verdict.Reason}",
            _ => $"{refused.Count} of the selected programs will be left. '{refused[0].Entry.Name}': {refused[0].Judgement.Verdict.Reason}",
        };

        return new UninstallSelection(runnable, string.Join(" ", new[] { runs, left }.OfType<string>()));
    }
}
