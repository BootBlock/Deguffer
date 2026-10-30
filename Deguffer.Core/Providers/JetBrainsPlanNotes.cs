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
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, unreadable == 1
                ? "1 replaced IDE version was left alone because part of its folder could not be read, so how recently it was used is not known."
                : $"{unreadable} replaced IDE versions were left alone because part of each folder could not be read, so how recently they were used is not known."));
        }

        if (Count(VersionVerdict.StandingUnknown) is > 0 and var unknown)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, unknown == 1
                ? "1 IDE version was left alone because whether it is still installed could not be read."
                : $"{unknown} IDE versions were left alone because whether they are still installed could not be read."));
        }

        if (Count(VersionVerdict.RecentlyUsed) is > 0 and var recent)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, recent == 1
                ? "1 replaced IDE version changed within the last 180 days, so it is kept until the IDE would delete it itself."
                : $"{recent} replaced IDE versions changed within the last 180 days, so they are kept until the IDE would delete them itself."));
        }

        if (Count(VersionVerdict.Link) is > 0 and var links)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, links == 1
                ? "1 IDE version's folder is a link to somewhere else, and Deguffer does not look through a link."
                : $"{links} IDE versions' folders are links to somewhere else, and Deguffer does not look through a link."));
        }

        if (scanNote is not null)
        {
            notes.Add(scanNote);
        }

        return notes;

        int Count(VersionVerdict verdict) => counts.GetValueOrDefault(verdict);
    }
}
