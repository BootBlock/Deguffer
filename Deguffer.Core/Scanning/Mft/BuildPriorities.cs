using Deguffer.Core.Diagnostics;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The read priority of every index build of one cache: low while a walk of another volume is in
/// flight on a disk the build's volume is on, and normal otherwise, or from the moment a caller needs
/// the build.
///
/// <para><b>Why a build yields.</b> Under <see cref="Configuration.ScanRoute.Auto"/> a build's reads
/// are speculative: every question it is asked is raced against a walk, and a build whose walk
/// answered first keeps reading for the questions after it (<see cref="MftVolumeIndexCache"/>). A walk
/// is an answer being waited for. Measured cold, a build of another volume on the walk's disk made the
/// walk 3.2 to 3.7 times as long as alone at normal priority, and 1.09 times at low (#478).</para>
///
/// <para><b>Why not to a walk of its own volume.</b> That is the walk the build races, and yielding
/// to it bought the walk nothing measurable: one read in flight on the walked volume itself changed
/// nothing, warm or cold (#477), because what slows that walk is the flush NTFS makes at the table's
/// first read (#544), and #478's own-volume rows put the walk at 1.21 and 1.41 times alone cold at
/// normal priority against 1.24 and 1.23 yielding. A system volume is asked about by most rows of a
/// planning pass, so a build yielding to its own walks read at low priority for most of its life, and
/// every question it would have answered was walked instead.</para>
///
/// <para>Measured over a whole elevated planning pass (<c>Deguffer.Benchmark plan</c>, #1081), the
/// two rules alternated in three rounds of three passes on a loaded machine. Yielding to its own
/// walks, the system volume's build read at low priority for 6.4 to 9.0 s and its index was ready
/// 18.5 to 28.3 s into the pass, which took 33.4 to 54.6 s. Yielding only to other volumes, it read
/// at low priority for 0.12 to 0.25 s and was ready 10.4 to 19.4 s in, and the pass took 11.5 to
/// 19.8 s, because a row that waits for the system volume's table ends the pass. Passes in which
/// every call to Windows slowed alike, under either rule, are left out.</para>
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
/// <para>A disk the volume or the walk did not name is taken to be shared, and a walk of a volume no
/// root could be named for is taken to be of another volume: yielding to a walk on another disk costs
/// the build the walk's length, and not yielding to one on the same disk costs the walk most of its
/// speed (<see cref="VolumeDisks.Contends"/>).</para>
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

        internal VolumeDisks Place { get; set; }

        internal bool Needed { get; set; }

        internal ReadPriority Applied { get; set; } = ReadPriority.Normal;

        /// <summary>Where the benchmark records each change of priority, or null where nothing records it.</summary>
        internal IndexBuildTrace? Trace { get; set; }
    }

    /// <summary>A walk in flight of <paramref name="volumes"/>, until disposed.</summary>
    public sealed class Walk(BuildPriorities owner, IReadOnlyList<VolumeDisks> volumes) : IDisposable
    {
        public void Dispose() => owner.End(this);

        /// <summary>Whether a build of <paramref name="built"/> yields to this walk: see <see cref="VolumeDisks.Contends"/>.</summary>
        internal bool Contends(VolumeDisks built)
        {
            foreach (var volume in volumes)
            {
                if (volume.Contends(built))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Let <paramref name="build"/>, reading <paramref name="volume"/>, which is <paramref name="place"/>,
    /// yield to the walks of other volumes on its disks from now on.
    /// </summary>
    public void Yield(Build build, IMftVolume volume, VolumeDisks place)
    {
        lock (_gate)
        {
            build.Volume = volume;
            build.Place = place;
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
    /// A walk of <paramref name="volumes"/> is in flight until the walk returned is disposed.
    /// </summary>
    public Walk Walking(IReadOnlyList<VolumeDisks> volumes)
    {
        var walk = new Walk(this, volumes);

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

        var wanted = !build.Needed && Contended(build.Place)
            ? ReadPriority.Low
            : ReadPriority.Normal;

        if (wanted != build.Applied)
        {
            volume.Prioritise(wanted);
            build.Applied = wanted;
            build.Trace?.Prioritised(wanted);
        }
    }

    private bool Contended(VolumeDisks built)
    {
        foreach (var walk in _walks)
        {
            if (walk.Contends(built))
            {
                return true;
            }
        }

        return false;
    }
}
