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
///
/// <para><b>Compared as folders.</b> A tool reports where it keeps its cache the way its own process
/// reached it, which may be through a letter <c>subst</c> made, or another mount of its volume, and a
/// row naming the same folder another way was left stating a figure the run had changed.</para>
///
/// <para><b>Looked up, not compared in pairs.</b> A page's rows can name thousands of locations, and
/// comparing each with every location the run changed costs their product. The changed locations are
/// filed under the folders that hold them and the folders they hold (<see cref="ChangedLocations"/>),
/// so each of a row's locations costs a walk up its own path. It takes a moment on a large page, and asks the machine where each location is, so the
/// shell calls it off the window's thread.</para>
/// </summary>
public static class RunChanges
{
    /// <param name="shown">Every finding on the page, in the order it is shown.</param>
    /// <param name="ran">
    /// The findings the run was authorised to clean, narrowed to what it was told to remove. A finding
    /// run only to prove what it left standing (<see cref="StandingProof"/>) removes nothing, and is
    /// not one of these.
    /// </param>
    /// <param name="volumes">Asked every other path each location is reachable at.</param>
    /// <returns>The providers to plan again, in the order <paramref name="shown"/> gives them.</returns>
    public static IReadOnlyList<ICleanupProvider> Stale(
        IReadOnlyList<Finding> shown, IReadOnlyList<Finding> ran, IVolumeInventory volumes)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(ran);
        ArgumentNullException.ThrowIfNull(volumes);

        var removing = ran.Where(f => f.Plan is { IsEmpty: false }).ToList();

        if (removing.Count == 0)
        {
            return [];
        }

        var ranIds = removing.Select(f => f.Provider.Id).ToHashSet(StringComparer.Ordinal);

        // Each location followed once, however many rows name it, and each drive asked where it is
        // mounted once, however many locations share it (G4).
        var followed = new Dictionary<string, ReachedFolder>(StringComparer.OrdinalIgnoreCase);
        var remembered = new RememberedMountPoints(volumes);

        IReadOnlyList<ReachedFolder> LocationsOf(RunReach reach) =>
        [
            .. reach.TargetedPaths.Concat(reach.ProbedPaths).Select(path =>
                followed.TryGetValue(path, out var folder) ? folder : followed[path] = ReachedFolder.At(path, remembered)),
        ];

        var changed = new ChangedLocations(LocationsOf(RunReach.Of([.. removing.Select(f => f.Plan!)])));

        return
        [
            .. shown
                .Where(f => ranIds.Contains(f.Provider.Id)
                            || f.Plan is { CountsLinksFromElsewhere: true }
                            || f.Plan is { } plan && LocationsOf(RunReach.Of([plan])).Any(changed.Overlaps))
                .Select(f => f.Provider),
        ];
    }

    /// <summary>
    /// The locations a run changed, filed so that whether a location is one of them, holds one or is
    /// held by one is a lookup at each folder on its own path rather than a comparison with each.
    ///
    /// <para>The index only narrows. Each location it names is asked the question itself, the same
    /// <see cref="ReachedFolder.Holds(ReachedFolder)"/> in both directions the pairs were asked, so
    /// the answer is the comparison's. A step's path may carry the extended-length prefix (§6.3) where
    /// a command's declared path does not, and <see cref="ReachedFolder"/> files and compares each
    /// without it.</para>
    /// </summary>
    private sealed class ChangedLocations
    {
        private readonly IReadOnlyList<ReachedFolder> _changed;

        /// <summary>Each changed location at every one of its places, for the locations inside one.</summary>
        private readonly FolderIndex<int> _at;

        /// <summary>Each changed location at every one of its places and every folder above them, for the locations holding one.</summary>
        private readonly FolderIndex<int> _within;

        public ChangedLocations(IReadOnlyList<ReachedFolder> changed)
        {
            _changed = changed;
            _at = new FolderIndex<int>(Enumerable.Range(0, changed.Count), position => changed[position].Places);
            _within = new FolderIndex<int>(Enumerable.Range(0, changed.Count), position => changed[position].PlacesAndAbove());
        }

        /// <summary>Whether <paramref name="location"/> and a changed location are the same folder, or one holds the other.</summary>
        public bool Overlaps(ReachedFolder location)
        {
            foreach (var place in location.Places)
            {
                foreach (var filed in _at.Holding(place))
                {
                    if (AnyOverlaps(filed, location))
                    {
                        return true;
                    }
                }

                if (AnyOverlaps(_within.At(place), location))
                {
                    return true;
                }
            }

            return false;
        }

        private bool AnyOverlaps(ReadOnlySpan<int> positions, ReachedFolder location)
        {
            foreach (var position in positions)
            {
                var changed = _changed[position];

                if (changed.Holds(location) || location.Holds(changed))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
