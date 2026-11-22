using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The sentences the superseded JetBrains row says about the versions it kept, one per reason, so
/// a row smaller than expected carries its own explanation.
/// </summary>
internal static class JetBrainsPlanNotes
{
    public static IReadOnlyList<PlanNote> For(
        IEnumerable<VersionVerdict> verdicts,
        IReadOnlyList<string> unreadableFolders,
        PlanNote? scanNote)
    {
        var counts = verdicts.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
        var notes = new List<PlanNote>();

        foreach (var folder in unreadableFolders)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Windows would not list '{folder}', so no IDE version inside it was looked at."));
        }

        if (Count(VersionVerdict.AgeUnknown) is > 0 and var unreadable)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, Counted.Form(unreadable,
                "1 replaced IDE version was left alone because part of its folder could not be read, so how recently it was used is not known.",
                $"{unreadable:N0} replaced IDE versions were left alone because part of each folder could not be read, so how recently they were used is not known.")));
        }

        if (Count(VersionVerdict.StandingUnknown) is > 0 and var unknown)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, Counted.Form(unknown,
                "1 IDE version was left alone because whether it is still installed could not be read.",
                $"{unknown:N0} IDE versions were left alone because whether they are still installed could not be read.")));
        }

        if (Count(VersionVerdict.RecentlyUsed) is > 0 and var recent)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, Counted.Form(recent,
                "1 replaced IDE version changed within the last 180 days, so it is kept until the IDE would delete it itself.",
                $"{recent:N0} replaced IDE versions changed within the last 180 days, so they are kept until the IDE would delete them itself.")));
        }

        if (Count(VersionVerdict.Link) is > 0 and var links)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, Counted.Form(links,
                "1 IDE version's folder is a link to somewhere else, and Deguffer does not look through a link.",
                $"{links:N0} IDE versions' folders are links to somewhere else, and Deguffer does not look through a link.")));
        }

        if (scanNote is not null)
        {
            notes.Add(scanNote);
        }

        return notes;

        int Count(VersionVerdict verdict) => counts.GetValueOrDefault(verdict);
    }
}
