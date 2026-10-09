using Deguffer.Core.Cloud;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// Reads the machine for what decides which copies can be kept (§7.4), and builds the
/// <see cref="CopyKeeping"/> that decides it: the temporary folder, where Storage's cleans delete,
/// the cloud folders, the program folders, Explore's policy, and what each copy's drive is.
///
/// <para>Apart from <see cref="DuplicateMarks"/> because reading is I/O and keeping is policy (G2):
/// the marks ask it when they are made and again at each confirmation, and it holds no mark.</para>
///
/// <para><b>Each read asks afresh.</b> Every place is followed to its final path again and the sync
/// roots are listed again. Each drive is taken as the search found it for the marks made straight
/// after the search, and asked of its disks again at a confirmation (<see cref="Now"/>), because a
/// disk moved from an internal bay into a USB dock keeps its volume and every file ID on it, and only
/// its bus says it can now be unplugged.</para>
/// </summary>
internal sealed class KeepingReader
{
    private readonly IUserEnvironment _environment;
    private readonly ICloudFiles _cloud;
    private readonly IVolumeInventory _volumes;
    private readonly Func<LocalVolume, VolumeMedia> _searchedMedia;
    private readonly Func<LocalVolume, VolumeMedia> _mediaNow;
    private readonly FileInformation _files;

    /// <param name="searchedMedia">
    /// What a volume's disks are as the search just found them (<see cref="VolumeMediaCache.Of"/>),
    /// for the marks made straight after it, which do not ask a device the search has just asked.
    /// </param>
    /// <param name="mediaNow">
    /// What a volume's disks are now, asked of the device again (<see cref="VolumeMediaCache.Now"/>),
    /// for the keeping rule judged again at a confirmation.
    /// </param>
    /// <param name="files">Where each place is followed to its final path, so a test can stand for a junction.</param>
    public KeepingReader(
        IUserEnvironment environment,
        ICloudFiles cloud,
        IVolumeInventory volumes,
        Func<LocalVolume, VolumeMedia> searchedMedia,
        Func<LocalVolume, VolumeMedia> mediaNow,
        FileInformation files)
    {
        _environment = environment;
        _cloud = cloud;
        _volumes = volumes;
        _searchedMedia = searchedMedia;
        _mediaNow = mediaNow;
        _files = files;
    }

    /// <summary>The keeping rule for the marks made straight after the search, with each drive as the search found it.</summary>
    /// <param name="programs">Every folder a program is installed in, which a copy is refused in.</param>
    public CopyKeeping AfterTheSearch(
        ExploreActionPolicy policy,
        IReadOnlyList<StorageClean> cleans,
        IReadOnlyList<ProgramFolder> programs,
        IEnumerable<DuplicateGroup> groups) =>
        Read(policy, cleans, programs, groups, _searchedMedia);

    /// <summary>The keeping rule as the machine is now, with each drive's disks asked again.</summary>
    /// <param name="programs">Every folder a program is installed in, which a copy is refused in.</param>
    public CopyKeeping Now(
        ExploreActionPolicy policy,
        IReadOnlyList<StorageClean> cleans,
        IReadOnlyList<ProgramFolder> programs,
        IEnumerable<DuplicateGroup> groups) =>
        Read(policy, cleans, programs, groups, _mediaNow);

    private CopyKeeping Read(
        ExploreActionPolicy policy,
        IReadOnlyList<StorageClean> cleans,
        IReadOnlyList<ProgramFolder> programs,
        IEnumerable<DuplicateGroup> groups,
        Func<LocalVolume, VolumeMedia> media)
    {
        // Asked once a volume here, off the UI thread, so the questions a page asks as it draws never
        // wait on a device.
        Dictionary<string, StorageMedia> classes = new(StringComparer.OrdinalIgnoreCase);

        foreach (var volume in groups.SelectMany(group => group.Files).Select(copy => copy.Volume).Distinct())
        {
            classes[volume.RootPath] = media(volume).Class;
        }

        var temporary = ResolvedPlaces.Resolve(
            [(CleanedPlace.Whole(_environment.TempPath),
              "This is in your temporary folder, which programs and Storage empty, so it is not counted on as the copy kept.")],
            _volumes,
            _files);

        var cleaned = ResolvedPlaces.Resolve(
            cleans.Select(clean => (clean.Place,
                $"Storage's '{clean.Clean}' clean can delete what is here, so it is not counted on as the copy kept.")),
            _volumes,
            _files);

        var cloudFolders = _cloud.SyncRoots() is { } roots
            ? ResolvedPlaces.Resolve(
                roots.Select(root => (CleanedPlace.Whole(root.Path),
                    $"This is in '{root.DisplayName}', a cloud folder: removing it removes it from every device that "
                    + "syncs the folder, and another device can remove it from here.")),
                _volumes,
                _files)
            : null;

        return new CopyKeeping(
            new CopyRefusals(policy, programs),
            temporary,
            cleaned,
            cloudFolders,
            volume => classes.TryGetValue(volume.RootPath, out var known) ? known : StorageMedia.Unknown);
    }

    /// <summary>
    /// The folder a rule names (<see cref="MarkingRule.KeepInFolder"/>, <see cref="MarkingRule.MarkInFolder"/>),
    /// at every path it is reachable at, its final path included, as the copies' paths are.
    /// </summary>
    public ResolvedPlaces Folder(string folder) =>
        ResolvedPlaces.Resolve([(CleanedPlace.Whole(folder), folder)], _volumes, _files);

    /// <summary>Program folders read now, with any the search knew that the read no longer names.</summary>
    /// <param name="known">What the search read, kept because a list Windows will not read now names nothing.</param>
    public IReadOnlyList<ProgramFolder> ProgramsNow(MachineProtections protections, IReadOnlyList<ProgramFolder> known, CancellationToken ct)
    {
        List<ProgramFolder> programs = [.. protections.ProgramFolders(_files, ct)];
        programs.AddRange(known.Where(folder => !programs.Exists(folder.IsSameAs)));

        return programs;
    }
}
