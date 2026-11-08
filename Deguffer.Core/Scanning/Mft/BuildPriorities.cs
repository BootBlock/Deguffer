namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The read priority of every index build of one cache: low while a walk is in flight on a disk the
/// build's volume is on, and normal otherwise, or from the moment a caller needs the build.
///
/// <para><b>Why a build yields.</b> Under <see cref="Configuration.ScanRoute.Auto"/> a build's reads
/// are speculative: every question it is asked is raced against a walk, and a build whose walk
/// answered first keeps reading for the questions after it (<see cref="MftVolumeIndexCache"/>). A walk
/// is an answer being waited for. Measured cold, a build of another volume on the walk's disk made the
/// walk 3.2 to 3.7 times as long as alone at normal priority, and 1.09 times at low (#478).</para>
///
/// <para><b>Why only while a walk is in flight.</b> A low-priority read waits behind everything else
/// the disk is serving, so the same build alone took 23.6 to 33.6 seconds at low priority against 4.4
/// to 4.6 at normal, cold, and 22.5 to 25.8 against 6.1 to 8.0 warm. A build yielding with no walk to
/// yield to would only answer later, and the table is what answers a system volume's questions
/// fastest.</para>
///
/// <para><b>Why it stops yielding once needed.</b> A caller that waits for the table with no walk of
/// its own beside it, because its walk could not reach its path or it does not race, waits on the one
/// route still answering. Another walk on the same disk must not slow that wait, so a needed build
/// reads at normal priority for the rest of its life.</para>
///
/// <para>A disk the volume or the walk did not name is taken to be shared: yielding to a walk on
/// another disk costs the build the walk's length, and not yielding to one on the same disk costs the
/// walk most of its speed.</para>
///
/// <para>One lock for every build and walk, held across the priority calls, so two changes never apply
/// out of order. Each call is one system call on a handle already open.</para>
/// </summary>
internal sealed class BuildPriorities
{
    private readonly Lock _gate = new();
    private readonly List<Build> _builds = [];
    private readonly List<Walk> _walks = [];

    /// <summary>One volume's build, from before it starts, so a caller can need it before it opens.</summary>
    public sealed class Build
    {
        internal IMftVolume? Volume { get; set; }

        internal IReadOnlyList<int>? Disks { get; set; }

        internal bool Needed { get; set; }

        internal ReadPriority Applied { get; set; } = ReadPriority.Normal;
    }

    /// <summary>A walk in flight on <see cref="Disks"/>, or on disks unknown where null, until disposed.</summary>
    public sealed class Walk(BuildPriorities owner, IReadOnlyList<int>? disks) : IDisposable
    {
        internal IReadOnlyList<int>? Disks => disks;

        public void Dispose() => owner.End(this);
    }

    /// <summary>
    /// Let <paramref name="build"/>, reading <paramref name="volume"/> on <paramref name="disks"/>,
    /// yield to the walks on those disks from now on. Null disks are unknown.
    /// </summary>
    public void Yield(Build build, IMftVolume volume, IReadOnlyList<int>? disks)
    {
        lock (_gate)
        {
            build.Volume = volume;
            build.Disks = disks;
            _builds.Add(build);
            Apply(build);
        }
    }

    /// <summary>A caller waits for <paramref name="build"/> alone: normal priority for the rest of its life.</summary>
    public void Need(Build build)
    {
        lock (_gate)
        {
            build.Needed = true;
            Apply(build);
        }
    }

    /// <summary>
    /// A walk of a volume on <paramref name="disks"/>, or on disks unknown where null, is in flight
    /// until the walk returned is disposed.
    /// </summary>
    public Walk Walking(IReadOnlyList<int>? disks)
    {
        var walk = new Walk(this, disks);

        lock (_gate)
        {
            _walks.Add(walk);
            ApplyAll();
        }

        return walk;
    }

    /// <summary>
    /// Forget every build, as the cache drops them. Each is ending by then, and a priority asked of it
    /// afterwards would reach a closed volume, which ignores it.
    /// </summary>
    public void Forget()
    {
        lock (_gate)
        {
            _builds.Clear();
        }
    }

    private void End(Walk walk)
    {
        lock (_gate)
        {
            if (_walks.Remove(walk))
            {
                ApplyAll();
            }
        }
    }

    private void ApplyAll()
    {
        foreach (var build in _builds)
        {
            Apply(build);
        }
    }

    private void Apply(Build build)
    {
        if (build.Volume is not { } volume)
        {
            return;
        }

        var wanted = !build.Needed && _walks.Exists(walk => Shared(walk.Disks, build.Disks))
            ? ReadPriority.Low
            : ReadPriority.Normal;

        if (wanted != build.Applied)
        {
            volume.Prioritise(wanted);
            build.Applied = wanted;
        }
    }

    private static bool Shared(IReadOnlyList<int>? walked, IReadOnlyList<int>? built) =>
        walked is null || built is null || walked.Any(built.Contains);
}
