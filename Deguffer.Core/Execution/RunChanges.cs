using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Which findings a clean left describing a disk that is no longer there, so the re-plan after it
/// measures those and leaves every other row as it stands.
///
/// <para><b>More than the rows that ran.</b> Providers are not disjoint. NuGet's own command empties
/// <c>%TEMP%\NuGetScratch</c>, inside the folder the temporary files row empties, and removing a project's
/// <c>node_modules</c> releases pnpm store files that were linked into it. A re-plan of the cleaned
/// rows alone would leave the neighbours stating figures the run had changed, beside a Clean button
/// that would act on them.</para>
///
/// <para>So a finding is stale when its provider ran, when one of its locations and one of the run's
/// are the same folder or one holds the other, or when its figure counts links from elsewhere and
/// the run removed anything at all. A location is a path a step deletes or a path a tool's own
/// command was declared to reach, which is <see cref="RunReach"/>'s reading of a plan.</para>
/// </summary>
public static class RunChanges
{
    /// <param name="shown">Every finding on the page, in the order it is shown.</param>
    /// <param name="ran">
    /// The findings the run was authorised to clean, narrowed to what it was told to remove. A finding
    /// run only to prove what it left standing (<see cref="StandingProof"/>) removes nothing, and is
    /// not one of these.
    /// </param>
    /// <returns>The providers to plan again, in the order <paramref name="shown"/> gives them.</returns>
    public static IReadOnlyList<ICleanupProvider> Stale(IReadOnlyList<Finding> shown, IReadOnlyList<Finding> ran)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(ran);

        var removing = ran.Where(f => f.Plan is { IsEmpty: false }).ToList();

        if (removing.Count == 0)
        {
            return [];
        }

        var ranIds = removing.Select(f => f.Provider.Id).ToHashSet(StringComparer.Ordinal);
        var changed = LocationsOf(RunReach.Of([.. removing.Select(f => f.Plan!)]));

        return
        [
            .. shown
                .Where(f => ranIds.Contains(f.Provider.Id)
                            || f.Plan is { CountsLinksFromElsewhere: true }
                            || f.Plan is { } plan && Overlaps(LocationsOf(RunReach.Of([plan])), changed))
                .Select(f => f.Provider),
        ];
    }

    /// <summary>
    /// A step's path may carry the extended-length prefix (§6.3) where a command's declared path does
    /// not, so every location is compared in display form.
    /// </summary>
    private static IReadOnlyList<string> LocationsOf(RunReach reach) =>
        [.. reach.TargetedPaths.Concat(reach.ProbedPaths).Select(LongPath.Display)];

    private static bool Overlaps(IReadOnlyList<string> these, IReadOnlyList<string> those) =>
        these.Any(a => those.Any(b => LongPath.Contains(a, b) || LongPath.Contains(b, a)));
}
