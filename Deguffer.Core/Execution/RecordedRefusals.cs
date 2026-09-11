using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// A finished plan with whatever Windows still refuses taken out of it, wherever a previous clean of
/// the same location was refused.
///
/// <para><b>Applied to every plan on its way out of a provider, not by each provider.</b> The defect
/// it closes was in none of them. A temporary folder showed it first — a quarter of a million files
/// in guarded browser profiles, offered by every preview, refused by every clean, and offered again —
/// but any location Deguffer deletes itself would do the same with a file it may not remove. See
/// <see cref="RefusalRecord"/> for why only the places a clean was refused are asked again, and
/// <see cref="DeletionProbe"/> for the one refusal the question cannot see.</para>
/// </summary>
internal static class RecordedRefusals
{
    /// <summary>
    /// How many of the places still refused a note names before counting the rest: enough for a
    /// reader to recognise what is holding the space, without a sentence two thousand names long.
    /// </summary>
    private const int NamedPlaces = 3;

    public static CleanupPlan Apply(CleanupPlan plan, RefusalRecord record, IFileSystem fs, CancellationToken ct)
    {
        var refused = Refusals.None;
        var places = new List<(string Place, Refusals Refused)>();
        var steps = new List<CleanupStep>(plan.Steps.Count);

        foreach (var step in plan.Steps)
        {
            // A Recycle Bin is emptied by the shell rather than by Deguffer's removal, so no refusal
            // was ever recorded against one, and nothing here knows how the shell refuses.
            if (step is not DeleteStep delete
                || step is EmptyRecycleBinStep
                || record.At(delete.Path) is not { Count: > 0 } recorded)
            {
                steps.Add(step);
                continue;
            }

            var found = RefusalCheck.Of(delete, recorded, plan.Keep, fs, ct);

            if (found.Refused.IsEmpty)
            {
                steps.Add(step);
                continue;
            }

            steps.Add(step with
            {
                Estimated = step.Estimated - ScanSize.FromLengths(found.Refused.Bytes),
                Refused = found.Refused,
            });

            refused += found.Refused;
            places.AddRange(found.Places);
        }

        return refused.IsEmpty
            ? plan
            : plan with { Steps = steps, Notes = [.. plan.Notes, .. Notes(refused, places)] };
    }

    /// <summary>
    /// One note per reason, because they ask different things of the reader — see
    /// <see cref="RefusalReason"/>. The refusal that waiting will not change is the warning: only the
    /// user can do anything about it, and nothing on the row otherwise says why its size fell.
    /// </summary>
    private static IEnumerable<PlanNote> Notes(Refusals refused, IReadOnlyList<(string Place, Refusals Refused)> places)
    {
        if (refused.Denied.Files > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Warning,
                $"Windows would not let Deguffer remove {FreeSpace.Format(refused.Denied.Bytes)} of this "
                + $"({refused.Denied.Files:N0} file(s){Where(places, p => p.Denied)}) when it last "
                + "cleaned, and still refuses, so the size shown leaves it out. Deguffer tries again each "
                + "time it cleans.");
        }

        if (refused.InUse.Files > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"Another program had {FreeSpace.Format(refused.InUse.Bytes)} of this open "
                + $"({refused.InUse.Files:N0} file(s){Where(places, p => p.InUse)}) when Deguffer "
                + "last cleaned, and still does, so the size shown leaves it out.");
        }
    }

    /// <summary>
    /// The places holding one kind of refusal, largest first. Driving the real window settled the
    /// order: alphabetically, the three names a reader saw were whichever places happened to sort
    /// first, which says nothing about where the refused space is.
    /// </summary>
    private static string Where(
        IReadOnlyList<(string Place, Refusals Refused)> places,
        Func<Refusals, RefusalTally> kind)
    {
        var named = places
            .Select(p => (p.Place, Tally: kind(p.Refused)))
            .Where(p => p.Tally.Files > 0)
            .OrderByDescending(p => p.Tally.Bytes)
            .ThenBy(p => p.Place, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Place)
            .ToList();

        var rest = named.Count - NamedPlaces;

        return ", in "
            + string.Join(", ", named.Take(NamedPlaces).Select(p => $"'{Path.GetFileName(Path.TrimEndingDirectorySeparator(p))}'"))
            + (rest > 0 ? $" and {rest:N0} more" : string.Empty);
    }
}
