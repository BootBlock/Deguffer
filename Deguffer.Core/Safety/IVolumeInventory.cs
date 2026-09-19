namespace Deguffer.Core.Safety;

/// <summary>
/// The parts of a volume's <c>GetVolumeInformation</c> flag word that Deguffer reads.
///
/// <para>Deliberately partial. Windows documents around twenty bits there and two of them decide
/// anything here, so naming the rest would invite a reader to take this for the Win32 constant set
/// and branch on a bit nothing has ever measured. The word is carried unmasked, so a bit with no
/// name here is still present in the value.</para>
/// </summary>
[Flags]
public enum VolumeFeatures : uint
{
    /// <summary>Nothing, which is also what a volume that would not answer is recorded as.</summary>
    None = 0,

    /// <summary>
    /// <c>FILE_SUPPORTS_REPARSE_POINTS</c>. Every local NTFS volume has it, and a volume without it
    /// cannot hold a link, a junction or a Cloud Files placeholder.
    /// </summary>
    ReparsePoints = 0x0000_0080,

    /// <summary>
    /// <c>FILE_SUPPORTS_REMOTE_STORAGE</c>. The volume can hold data that is not on this machine, so
    /// reading a file may fetch it from somewhere else first.
    /// </summary>
    RemoteStorage = 0x0000_0100,
}

/// <summary>
/// One volume the machine has mounted, wherever it is mounted.
/// </summary>
/// <param name="RootPath">
/// The mount point callers act on and show, in <c>D:\</c> or <c>C:\Mount\</c> form.
///
/// <para>A volume can be mounted in more than one place at once, and Windows draws no distinction
/// between a drive letter and a folder mount point. One of them still has to be the one a plan
/// targets and the drive picker offers, or the same volume would be listed twice and its Recycle
/// Bin would be planned twice under two names. <see cref="MountPoints"/> is the whole set, for the
/// one question that needs it: whether a given path is on this volume.</para>
/// </param>
/// <param name="Kind">
/// Fixed, removable, network, and so on. Reported rather than filtered, because which kinds a
/// provider may act on is a safety decision belonging to that provider — and a seam that filtered
/// would leave the decision untestable, since no fake could then present the kind being refused.
/// </param>
/// <param name="IsReady">
/// Whether the volume can be read at all. An optical drive with no disc and a card reader with no
/// card are both mounted and both answer no.
/// </param>
/// <param name="Label">
/// What the volume is called, or null where it has no label, would not say, or was not asked. Null
/// rather than an empty string, so "unlabelled" is one case at every caller instead of two.
/// </param>
/// <param name="TotalBytes">Capacity, on the same terms.</param>
/// <param name="FreeBytes">
/// What is left of that capacity for this user, on the same terms. The figure a quota allows rather
/// than the raw free space, matching <c>FreeSpace.ForPath</c>: the two are read by the same app,
/// through the same call, and must not disagree.
/// </param>
/// <param name="Features">
/// What the volume says it supports, or <see cref="VolumeFeatures.None"/> where it would not say or
/// was not asked. Reported rather than filtered, for the reason <paramref name="Kind"/> is.
/// </param>
/// <param name="AlsoMountedAt">
/// Every other path this volume is reachable at, or null where there is none — which is the
/// ordinary case, since most volumes wear one drive letter and nothing else.
/// </param>
public readonly record struct LocalVolume(
    string RootPath,
    DriveType Kind,
    bool IsReady,
    string? Label = null,
    long? TotalBytes = null,
    long? FreeBytes = null,
    VolumeFeatures Features = VolumeFeatures.None,
    IReadOnlyList<string>? AlsoMountedAt = null)
{
    /// <summary>
    /// Every path this volume is reachable at, <see cref="RootPath"/> first.
    ///
    /// <para>Read by <see cref="HostVolume.For"/>, which is the one caller that asks about a path
    /// rather than about the volume: a folder-mounted volume answers for the paths below its mount
    /// point and nothing else does, so a set that named only the root would hand those paths to the
    /// volume the mount point sits on.</para>
    /// </summary>
    public IEnumerable<string> MountPoints
    {
        get
        {
            yield return RootPath;

            foreach (var mountPoint in AlsoMountedAt ?? [])
            {
                yield return mountPoint;
            }
        }
    }

    /// <summary>
    /// Whether this volume's contents are somewhere else, so that enumerating it downloads the
    /// user's files instead of measuring them.
    ///
    /// <para>A cloud client that mounts its storage through its own driver is a
    /// <see cref="DriveType.Fixed"/>, ready volume that no per-entry test can tell from a disk. A
    /// Google Drive mount's top-level entries were measured carrying nothing but ordinary hidden,
    /// system and normal attributes: no <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c>, no
    /// <c>FILE_ATTRIBUTE_OFFLINE</c>, no reparse point. A walker cannot defend itself entry by
    /// entry there, so the location is what has to be refused.</para>
    ///
    /// <para><b>This recognises a mount that advertises the flag, not every cloud client.</b>
    /// Advertising remote storage is a third-party driver's own choice, and nothing in Win32
    /// obliges one to. A mount that does not say so is walked, as every volume was before this
    /// reading existed. Google Drive is the one product whose flag word was measured.</para>
    ///
    /// <para><b>Why these two bits and not the format.</b> The mount advertises remote storage and
    /// supports neither reparse points nor sparse files, and the Cloud Files API builds a
    /// placeholder as a reparse point on a sparse file and supports NTFS alone. So a volume with
    /// this pair cannot be hosting per-file placeholders that would explain the flag innocently,
    /// which is what makes the pair structural rather than incidental. Measured as
    /// <c>0x00000106</c> on a Google Drive mount against <c>0x03E72EFF</c> on seven local NTFS
    /// volumes of the same machine.</para>
    ///
    /// <para><b>The format alone would refuse real disks.</b> A fixed volume that is not NTFS is
    /// also a FAT32 or exFAT disk, a card reader reporting itself fixed, or a mounted image — all
    /// of which are ordinary to walk. A refusal that reached them would take away the tool's
    /// purpose to fix a hazard they do not have.</para>
    /// </summary>
    public bool StoresContentRemotely =>
        Features.HasFlag(VolumeFeatures.RemoteStorage) && !Features.HasFlag(VolumeFeatures.ReparsePoints);
}

/// <summary>
/// The machine's volumes, behind an interface so a provider that works per volume is testable
/// against directories we build rather than against whatever drives the developer happens to have.
///
/// Separate from <see cref="IUserEnvironment"/> rather than a member on it: that interface is the
/// signed-in user — their profile directories, their <c>PATH</c>, their environment — and the set
/// of mounted volumes is a fact about the hardware instead. Describing one type as "the user and
/// the disks" is G1's own test for two types.
/// </summary>
public interface IVolumeInventory
{
    IReadOnlyList<LocalVolume> Volumes { get; }

    /// <summary>
    /// Where the volume holding <paramref name="path"/> is mounted, asked of the machine at the
    /// moment of the call rather than read from <see cref="Volumes"/>, or null where it will not say.
    ///
    /// <para>Live because its caller is <see cref="VolumeRoot"/>, which has to be right about a
    /// volume mounted a moment ago, and the list is remembered until the next
    /// <see cref="Invalidate"/>. A volume mounted at a folder after the list was read would
    /// otherwise have its paging file and its Recycle Bin read as ordinary folders of the disk the
    /// folder sits on.</para>
    ///
    /// <para>The answer is not always a prefix of the path. A path that passes through a junction
    /// is answered with the volume on the junction's far side, so a caller that reads a position
    /// from the answer has to check that it is one.</para>
    /// </summary>
    string? MountPointOf(string path);

    /// <summary>
    /// Discard the remembered list, so a drive mounted while the app was open is seen on the next
    /// preview. Called at the start of a planning pass, as <see cref="IUserEnvironment.Invalidate"/>
    /// is.
    /// </summary>
    void Invalidate();
}

/// <inheritdoc />
public sealed class VolumeInventory : IVolumeInventory
{
    /// <summary>
    /// The one instance the app runs with (G5). Stateless apart from the list it remembers, and
    /// that list describes the machine rather than any one caller.
    /// </summary>
    public static readonly VolumeInventory Current = new();

    private readonly Lock _gate = new();

    private IReadOnlyList<LocalVolume>? _volumes;

    /// <summary>
    /// Memoised for the life of a planning pass. Enumerating volumes probes every mounted device,
    /// which for an optical drive means waiting on the hardware, and a provider asks the same
    /// question from both <c>IsPresentAsync</c> and <c>PlanAsync</c> (G4).
    /// </summary>
    public IReadOnlyList<LocalVolume> Volumes
    {
        get
        {
            lock (_gate)
            {
                return _volumes ??= Read();
            }
        }
    }

    public string? MountPointOf(string path) => VolumeCalls.MountPointOf(path);

    public void Invalidate()
    {
        lock (_gate)
        {
            _volumes = null;
        }
    }

    /// <summary>
    /// The machine's volumes, each with every path it is mounted at.
    ///
    /// <para><b>Volumes first, then the letters no volume claimed.</b> <c>FindFirstVolume</c> names
    /// every volume of this machine and <c>GetVolumePathNamesForVolumeName</c> names every place
    /// each one is mounted, which is the only route that sees a volume mounted at a folder. It sees
    /// no mapped network drive and no <c>subst</c>, though: those are letters standing for
    /// somewhere else rather than volumes, and they were in the list this method used to build from
    /// <c>DriveInfo.GetDrives</c>. So the letters are read as well, and each one no volume already
    /// answers for is described in its own right.</para>
    ///
    /// <para>That union is also the degradation. Where the volume enumeration answers nothing —
    /// an API that would not start — every letter is unclaimed and the result is exactly the list
    /// this built before, rather than an empty one.</para>
    /// </summary>
    private static IReadOnlyList<LocalVolume> Read()
    {
        var volumes = new List<LocalVolume>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in VolumeCalls.Names())
        {
            var mountPoints = VolumeCalls.MountPointsOf(name);

            if (mountPoints.Count == 0)
            {
                // Mounted nowhere: a recovery partition, or a volume whose letter was removed. No
                // path names it, so no provider could target it and the picker could not offer it.
                continue;
            }

            var ordered = Ordered(mountPoints);

            claimed.UnionWith(ordered);
            volumes.Add(Describe(ordered));
        }

        string[] letters;
        try
        {
            letters = Environment.GetLogicalDrives();
        }
        catch (IOException)
        {
            // The volumes above are still whole, and are the part every safety rule reads. Only
            // network mappings would have been added here, and no rule that decides a deletion
            // acts on one.
            return volumes;
        }

        foreach (var letter in letters)
        {
            if (claimed.Add(letter))
            {
                volumes.Add(Describe([letter]));
            }
        }

        return volumes;
    }

    /// <summary>
    /// The mount points with the one callers act on first: the shortest, which is a drive letter
    /// wherever the volume has one, since <c>D:\</c> is three characters and no folder mount point
    /// can be fewer than five.
    ///
    /// <para>A rule rather than the order Windows returned, because that order is not documented
    /// and a volume whose root path moved between two reads would take the drive picker's selection
    /// and a plan's targets with it. Ties go to the earlier path, so the choice is settled even
    /// between two folder mount points of the same length.</para>
    /// </summary>
    internal static IReadOnlyList<string> Ordered(IReadOnlyList<string> mountPoints) =>
        mountPoints.Count == 1
            ? mountPoints
            : [.. mountPoints.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Whether the mount point can be read at all is the one thing that gates everything else here:
    /// an empty optical drive is mounted, answers no, and has no label, size or flags to give.
    ///
    /// <para>A network volume is described by its mount point alone. The label and the two space
    /// figures each cost a round trip to the server, <see cref="Volumes"/> is read under a lock and
    /// on the UI thread, and no caller wants them for a share: the picker refuses network volumes
    /// outright and <c>RecycleBinProvider</c> takes fixed ones only. This declines a cost, and
    /// filters nothing — which kinds a caller may act on stays that caller's decision.</para>
    /// </summary>
    private static LocalVolume Describe(IReadOnlyList<string> mountPoints)
    {
        var root = mountPoints[0];
        var elsewhere = mountPoints.Count > 1 ? mountPoints.Skip(1).ToArray() : null;
        var kind = VolumeCalls.KindOf(root);
        var ready = LongPath.DirectoryExists(root);

        if (!ready || kind == DriveType.Network)
        {
            return new LocalVolume(root, kind, ready, AlsoMountedAt: elsewhere);
        }

        // The label and the flags come from one call, so a volume that refuses cannot be recorded
        // as having answered one and not the other. Flags of None reach StoresContentRemotely as
        // "said nothing about remote storage", which is walked — the same answer a volume with no
        // such driver gives.
        var (label, features) = VolumeCalls.InformationOf(root);
        var space = VolumeCalls.SpaceOf(root);

        return new LocalVolume(
            root,
            kind,
            IsReady: true,
            Label: label,
            TotalBytes: space?.Total,
            FreeBytes: space?.Free,
            Features: features,
            AlsoMountedAt: elsewhere);
    }
}
