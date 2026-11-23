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
///
/// <para><b>A walk is placed only once a build could yield to it.</b> Which volumes and disks a walk
/// reads is asked of each of its paths, and a walk with no build in flight beside it has no priority
/// to change, which is every walk of an unelevated pass and of the walk-only route (#1065). So a walk
/// is placed when it starts only where a build is in flight, and a build that starts during walks not
/// yet placed places them before it yields. Either way the asking happens outside the lock, because
/// the first question about a volume can wait on its device. While any build is in flight, every walk
/// in flight is placed, so a priority is never decided under the lock from a walk not yet
/// asked about. A build that ends stops counting (<see cref="Ended"/>).</para>
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

    /// <summary>
    /// A walk in flight of the volumes <paramref name="volumesOf"/> names, until disposed. The volumes
    /// are asked for only once a build could yield to the walk (<see cref="IsPlaced"/>).
    /// </summary>
    public sealed class Walk(BuildPriorities owner, Func<IReadOnlyList<VolumeDisks>> volumesOf) : IDisposable
    {
        private IReadOnlyList<VolumeDisks>? _volumes;

        public void Dispose() => owner.End(this);

        /// <summary>Whether the walk's volumes have been asked for.</summary>
        internal bool IsPlaced => Volatile.Read(ref _volumes) is not null;

        /// <summary>
        /// The walk's volumes, asked for the first time they are needed. Two threads asking at once may
        /// both ask, and one answer is kept: both describe the same paths.
        /// </summary>
        internal IReadOnlyList<VolumeDisks> Volumes => LazyInitializer.EnsureInitialized(ref _volumes, volumesOf);

        /// <summary>Whether a build of <paramref name="built"/> yields to this walk: see <see cref="VolumeDisks.Contends"/>.</summary>
        internal bool Contends(VolumeDisks built)
        {
            foreach (var volume in Volumes)
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
        while (true)
        {
            Walk? unplaced;

            lock (_gate)
            {
                unplaced = _walks.Find(walk => !walk.IsPlaced);

                if (unplaced is null)
                {
                    build.Volume = volume;
                    build.Place = place;
                    _builds.Add(build);
                    Apply(build);
                    return;
                }
            }

            // Outside the lock, because asking what a path is on can wait on a device the first time,
            // and every walk and build of the pass takes this lock. See the class remarks.
            _ = unplaced.Volumes;
        }
    }

    /// <summary>
    /// <paramref name="build"/> has ended, and has no priority left to change, so the walks after it
    /// need not be placed for its sake.
    /// </summary>
    public void Ended(Build build)
    {
        lock (_gate)
        {
            _builds.Remove(build);
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
    /// A walk of the volumes <paramref name="volumesOf"/> names is in flight until the walk returned is
    /// disposed. They are asked for here only where a build is in flight: see the class remarks.
    /// </summary>
    public Walk Walking(Func<IReadOnlyList<VolumeDisks>> volumesOf)
    {
        var walk = new Walk(this, volumesOf);

        while (true)
        {
            lock (_gate)
            {
                if (_builds.Count == 0 || walk.IsPlaced)
                {
                    _walks.Add(walk);
                    ApplyAll();
                    return walk;
                }
            }

            // Outside the lock, as in Yield.
            _ = walk.Volumes;
        }
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
