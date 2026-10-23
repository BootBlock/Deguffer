namespace Deguffer.Core.Safety;

/// <inheritdoc />
public sealed class VolumeInventory : IVolumeInventory
{
    /// <summary>
    /// The one instance the app runs with (G5). Stateless apart from the list it remembers, and
    /// that list describes the machine rather than any one caller.
    /// </summary>
    public static readonly VolumeInventory Current = new(VolumeQueries.Machine);

    private readonly IVolumeQueries _queries;

    private readonly Lock _gate = new();

    private IReadOnlyList<LocalVolume>? _volumes;

    /// <summary>
    /// Moved on by each <see cref="Invalidate"/> and by each list <see cref="RefreshAsync"/> puts in
    /// place, so a reading can tell whether anything came between its start and its end.
    /// </summary>
    private long _generation;

    internal VolumeInventory(IVolumeQueries queries)
    {
        _queries = queries;
    }

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

    public IReadOnlyList<string> MountPointsOf(string mountPoint)
    {
        // GetVolumeNameForVolumeMountPoint answers only for a mount point named with its trailing
        // separator, and a caller holding C:\Mount means the same directory.
        var named = Path.EndsInDirectorySeparator(mountPoint) ? mountPoint : mountPoint + Path.DirectorySeparatorChar;

        return VolumeCalls.VolumeNameOf(named, out _) is { } name ? Ordered(VolumeCalls.MountPointsOf(name)) : [];
    }

    public string? SubstituteOf(string driveRoot) => VolumeCalls.SubstituteOf(driveRoot);

    public (long Total, long Free)? SpaceOf(string path) =>
        VolumeCalls.MountPointOf(path) is { } mountPoint ? VolumeCalls.SpaceOf(mountPoint) : null;

    public void Invalidate()
    {
        lock (_gate)
        {
            _volumes = null;
            _generation++;
        }
    }

    public async Task<IReadOnlyList<LocalVolume>> RefreshAsync(CancellationToken ct)
    {
        while (true)
        {
            long started;

            lock (_gate)
            {
                started = _generation;
            }

            // Outside the lock, so a reader of Volumes is answered from the old list meanwhile.
            var read = await Task.Run(Read, ct).ConfigureAwait(false);

            lock (_gate)
            {
                if (_generation == started)
                {
                    _volumes = read;
                    _generation++;

                    return read;
                }
            }

            ct.ThrowIfCancellationRequested();
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
    private IReadOnlyList<LocalVolume> Read()
    {
        var volumes = new List<LocalVolume>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in _queries.Names())
        {
            var mountPoints = _queries.MountPointsOf(name);

            if (mountPoints.Count == 0)
            {
                // Mounted nowhere: a recovery partition, or a volume whose letter was removed. No
                // path names it, so no provider could target it and the picker could not offer it.
                continue;
            }

            var ordered = Ordered(mountPoints);

            claimed.UnionWith(ordered);
            volumes.Add(Describe(ordered) with { VolumeName = name });
        }

        IReadOnlyList<string> letters;
        try
        {
            letters = _queries.Letters();
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
    /// <para>A network volume is described by its mount point and its kind alone, and its root is
    /// not probed (<see cref="VolumeReadiness.NotAsked"/>). The probe, the label and the two space
    /// figures each cost a round trip to the server, and a mapped share that is not there answers
    /// the probe only once the redirector gives up on it. No caller wants any of it for a share: the
    /// picker refuses network volumes outright and <c>RecycleBinProvider</c> takes fixed ones only.
    /// This declines a cost, and filters nothing — which kinds a caller may act on stays that
    /// caller's decision.</para>
    /// </summary>
    internal LocalVolume Describe(IReadOnlyList<string> mountPoints)
    {
        var root = mountPoints[0];
        var elsewhere = mountPoints.Count > 1 ? mountPoints.Skip(1).ToArray() : null;
        var kind = _queries.KindOf(root);

        if (kind == DriveType.Network)
        {
            return new LocalVolume(root, kind, VolumeReadiness.NotAsked, AlsoMountedAt: elsewhere);
        }

        // An empty drive answers ERROR_NOT_READY, which the probe reads as absent, so only a refusal
        // is left to mean that Windows would not say.
        var readiness = _queries.Probe(root) switch
        {
            PathPresence.Present => VolumeReadiness.Ready,
            PathPresence.Refused => VolumeReadiness.Refused,
            _ => VolumeReadiness.NoMedia,
        };

        if (readiness is not VolumeReadiness.Ready)
        {
            return new LocalVolume(root, kind, readiness, AlsoMountedAt: elsewhere);
        }

        // The label and the flags come from one call, so a volume that refuses cannot be recorded
        // as having answered one and not the other. A refusal is recorded as flags of None, which
        // reach StoresContentRemotely as "said nothing about remote storage" and so are walked — the
        // same answer a volume with no such driver gives — and as unanswered, so it is never taken
        // for a volume that said it cannot hold links.
        var (label, features, fileSystem) = _queries.InformationOf(root);
        var space = _queries.SpaceOf(root);

        return new LocalVolume(
            root,
            kind,
            VolumeReadiness.Ready,
            Label: label,
            TotalBytes: space?.Total,
            FreeBytes: space?.Free,
            Features: features ?? VolumeFeatures.None,
            AlsoMountedAt: elsewhere,
            FeaturesAnswered: features is not null,
            FileSystem: fileSystem);
    }
}
