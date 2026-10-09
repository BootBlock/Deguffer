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
/// <param name="Groups">The files that may match, by what was compared without reading their content.</param>
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
/// The stages of a duplicate search before any content is read (§7.4): resolves the locations,
/// decides what to pass over, reads each location's tree, keeps the files that may match, and
/// identifies them, grouped by their length, name and time. It reads no file's content, and opens a
/// file only for its attributes.
///
/// <para>Orchestration only. <see cref="SearchLocations"/> decides where a location is,
/// <see cref="ProgramFolders"/> and <see cref="ExploreActionPolicy"/> what is passed over,
/// <see cref="FileInformation"/> whether a volume's files can be identified,
/// <see cref="ExploreScanner"/> which route reads a tree, <see cref="CandidateWalk"/> which files are
/// kept, <see cref="CandidateIdentification"/> which are identified and
/// <see cref="CandidateGrouping"/> how they are grouped.</para>
///
/// <para><b>A volume whose files cannot be identified is not searched</b>, and each location on it
/// is named with the reason. Without an identity a file reached by two paths cannot be told from two
/// copies, which is how a finder offers a file as its own duplicate.</para>
/// </summary>
public sealed class CandidateFinder
{
    private const string Unidentifiable =
        "Windows would not say which file is which on this drive, so a file reached by two paths could not be told from two copies, and this location was not searched.";

    private readonly ExploreScanner _scanner;
    private readonly IVolumeInventory _volumes;
    private readonly IUninstallRegistry _registry;
    private readonly IUserEnvironment _environment;
    private readonly ISystemDirectories _system;
    private readonly FileInformation _files;

    public CandidateFinder(
        ExploreScanner scanner,
        IVolumeInventory volumes,
        IUninstallRegistry registry,
        IUserEnvironment environment,
        ISystemDirectories system)
        : this(scanner, volumes, registry, environment, system, FileInformation.Default)
    {
    }

    /// <param name="files">
    /// Where each location is opened to learn its final path and each file to learn its identity, so
    /// a test can make Windows refuse to open one location or describe one file, or stand for a
    /// volume that identifies its files by the older call or not at all, and see what the search does.
    /// </param>
    internal CandidateFinder(
        ExploreScanner scanner,
        IVolumeInventory volumes,
        IUninstallRegistry registry,
        IUserEnvironment environment,
        ISystemDirectories system,
        FileInformation files)
    {
        _scanner = scanner;
        _volumes = volumes;
        _registry = registry;
        _environment = environment;
        _system = system;
        _files = files;
    }

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

        var locations = SearchLocations.Resolve(search.Locations, _volumes, _files);
        var walk = new CandidateWalk(search, locations.UnresolvedReferences);

        var programs = search.SearchPassedOverPlaces
            ? new ProgramFolderReading([], [], [])
            : ProgramFolders.Read(_registry, _environment, _system, _volumes, locations.Locations, ct);

        var passedOver = search.SearchPassedOverPlaces ? null : new PassedOverPlaces(policy, programs.Folders);
        List<ResolvedLocation> roots = [];
        List<IdentityRoute> routes = [];
        Dictionary<LocalVolume, IdentityRoute?> routeOfVolume = [];
        List<UnsearchedLocation> unidentifiable = [];

        foreach (var root in locations.Roots)
        {
            // A folder given in both roles is a reference, so one whose reference was not resolved is
            // passed over even though it was resolved to be searched.
            if ((locations.UnresolvedReferences.WhyPassedOver(root.Folder) ?? passedOver?.WhyPassedOver(root.Folder)) is { } why)
            {
                walk.PassOver(new PassedOverPlace(root.Folder, why));
            }
            else if (RouteOf(root) is { } route)
            {
                roots.Add(root);
                routes.Add(route);
            }
            else
            {
                // Every location inside it is on the same volume, so none of them can be searched
                // either, and each is named: a reference among them is one no rule may mark around.
                unidentifiable.Add(new UnsearchedLocation(root.Given, Unidentifiable));
                unidentifiable.AddRange(locations.Within(root).Select(inner => new UnsearchedLocation(inner.Given, Unidentifiable)));
            }
        }

        // Asked once a volume and kept, so every file on one volume is identified by one route: the
        // two routes give a volume's serial number at different widths, and a file identified by
        // each would read as two files.
        IdentityRoute? RouteOf(ResolvedLocation root) =>
            routeOfVolume.TryGetValue(root.Volume, out var known)
                ? known
                : routeOfVolume[root.Volume] = _files.IdentityRouteOf(root.Folder);

        var scans = await _scanner.ScanFoldersAsync([.. roots.Select(root => root.Folder)], progress, ct).ConfigureAwait(false);

        for (var i = 0; i < roots.Count; i++)
        {
            walk.Read(scans[i], roots[i], locations.Within(roots[i]), passedOver?.Within(roots[i].Folder), routes[i], ct);
        }

        var identification = new CandidateIdentification(_files, search.Criteria);
        var groups = identification.Group(walk.Found, ct);

        return new CandidateFinding(
            groups,
            [.. locations.Unsearched, .. unidentifiable],
            walk.PassedOver,
            walk.Unread,
            [.. roots.Select((root, i) => new ReadLocation(root.Folder, scans[i].RouteNote))],
            programs.SetAside,
            programs.Unread,
            walk.LeftOut + identification.LeftOut);
    }
}
