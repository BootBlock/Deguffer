using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// A place inside a searched location that the search could not read, in whole or in part, and why,
/// in a sentence the page shows. Named so a search that skipped a place never reads as one that
/// found nothing there.
/// </summary>
public sealed record UnreadPlace(string Path, string Reason);

/// <summary>A location that was read, and what to say about the route it was read by.</summary>
/// <param name="RouteNote">The sentence <see cref="ScannedFolder.RouteNote"/> gives, or null where nothing needs saying.</param>
public sealed record ReadLocation(string Folder, string? RouteNote);

/// <summary>What <see cref="CandidateFinder.FindAsync"/> found, and everything it did not look at.</summary>
/// <param name="Groups">The files that may match, by what was compared without reading them.</param>
/// <param name="Unsearched">The chosen locations that were not searched, each with its reason.</param>
/// <param name="PassedOver">The places inside the locations that were passed over, each with its reason.</param>
/// <param name="Unread">The places inside the locations that could not be read, each with its reason.</param>
/// <param name="Read">Each location that was read, with the route it was read by.</param>
/// <param name="SetAside">The install locations that named nothing a search could pass over.</param>
/// <param name="UnreadProgramLists">The lists of installed programs Windows would not read.</param>
public sealed record CandidateFinding(
    IReadOnlyList<CandidateGroup> Groups,
    IReadOnlyList<UnsearchedLocation> Unsearched,
    IReadOnlyList<PassedOverPlace> PassedOver,
    IReadOnlyList<UnreadPlace> Unread,
    IReadOnlyList<ReadLocation> Read,
    IReadOnlyList<SetAsideInstallLocation> SetAside,
    IReadOnlyList<UninstallScope> UnreadProgramLists,
    LeftOutFiles LeftOut);

/// <summary>
/// The first stage of a duplicate search (§7.4): resolves the locations, decides what to pass over,
/// reads each location's tree and groups the files it keeps by length and name. It reads no file.
///
/// <para>Orchestration only. <see cref="SearchLocations"/> decides where a location is,
/// <see cref="ProgramFolders"/> and <see cref="ExploreActionPolicy"/> what is passed over,
/// <see cref="ExploreScanner"/> which route reads a tree, <see cref="CandidateWalk"/> which files are
/// kept and <see cref="CandidateGrouping"/> how they are grouped.</para>
/// </summary>
public sealed class CandidateFinder(
    ExploreScanner scanner,
    IVolumeInventory volumes,
    IUninstallRegistry registry,
    IUserEnvironment environment,
    ISystemDirectories system)
{
    /// <param name="policy">
    /// Explore's policy, built for this machine by <see cref="ExploreActionPolicy.ForAsync"/>, whose
    /// refusals at and below a place are what the search passes over.
    /// </param>
    public async Task<CandidateFinding> FindAsync(
        DuplicateSearch search,
        ExploreActionPolicy policy,
        IProgress<ExploreProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(policy);

        var locations = SearchLocations.Resolve(search.Locations, volumes);
        var walk = new CandidateWalk(search);

        var programs = search.SearchPassedOverPlaces
            ? new ProgramFolderReading([], [], [])
            : ProgramFolders.Read(registry, environment, system, volumes, locations.Locations, ct);

        var passedOver = search.SearchPassedOverPlaces ? null : new PassedOverPlaces(policy, programs.Folders);
        List<ResolvedLocation> roots = [];

        foreach (var root in locations.Roots)
        {
            if (passedOver?.WhyPassedOver(root.Folder) is { } why)
            {
                walk.PassOver(new PassedOverPlace(root.Folder, why));
            }
            else
            {
                roots.Add(root);
            }
        }

        var scans = await scanner.ScanFoldersAsync([.. roots.Select(root => root.Folder)], progress, ct).ConfigureAwait(false);

        for (var i = 0; i < roots.Count; i++)
        {
            walk.Read(scans[i], roots[i], locations.Within(roots[i]), passedOver?.Within(roots[i].Folder), ct);
        }

        return new CandidateFinding(
            CandidateGrouping.Group(walk.Found, search.Criteria),
            locations.Unsearched,
            walk.PassedOver,
            walk.Unread,
            [.. roots.Select((root, i) => new ReadLocation(root.Folder, scans[i].RouteNote))],
            programs.SetAside,
            programs.Unread,
            walk.LeftOut);
    }
}
