using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The scanner §5.5 asks for: read the MFT, fall back to a bounded parallel walk where that is not
/// possible, cache across runs, and stream partial results rather than blocking. What outlasts a run
/// is each path's size. The volume index is kept only for the planning pass that built it.
///
/// Choosing between the two routes is <see cref="VolumeRoutes"/>, which only this scanner and its
/// <see cref="HardLinks"/> use. Providers, the planner and the
/// executor ask <see cref="IDirectoryScanner"/> for a size and are told how it was obtained; none
/// of them contains a branch on which strategy is in play, because the answer depends on the volume
/// and the process token rather than on anything about a cache (G1, G2).
/// </summary>
public sealed class DirectoryScanner : IDirectoryScanner
{
    private readonly MftVolumeIndexCache _volumes;
    private readonly ScanEstimateCache? _estimates;
    private readonly ParallelEnumerationScanner _fallback;
    private readonly ScanTuner _tuner;
    private readonly VolumeRoutes _routes;

    public ScanTuner Tuning => _tuner;

    /// <summary>
    /// The sole-link figure (<see cref="HardLinkAwareScanner"/>) read from this scanner's volume
    /// indexes where something else is building them, and walked where nothing is. One for each
    /// scanner, built with it (G5), so every store measured beside this scanner's rows reads their
    /// index rather than walking file by file.
    /// </summary>
    public HardLinkAwareScanner HardLinks { get; }

    /// <param name="sources">
    /// Opens each volume's table. Never defaulted, so no scanner reads this machine's tables unless
    /// it was handed them, as <see cref="CreateDefault"/> hands it an environment's.
    /// </param>
    /// <param name="tuning">
    /// The route the user allows and the values each read runs with, asked as each measurement
    /// starts. <see cref="ScanTuner.Shipped"/> where none is given.
    /// </param>
    public DirectoryScanner(
        IMftSourceFactory sources,
        ScanEstimateCache? estimates = null,
        ParallelEnumerationScanner? fallback = null,
        ScanTuner? tuning = null)
    {
        _tuner = tuning ?? ScanTuner.Shipped;
        _volumes = new MftVolumeIndexCache(sources, _tuner);
        _estimates = estimates;
        _fallback = fallback ?? new ParallelEnumerationScanner(_tuner);
        _routes = new VolumeRoutes(_volumes, _tuner, _fallback, LinkCounting.EachName, _volumes.Get);
        HardLinks = new HardLinkAwareScanner(_tuner, _volumes);
    }

    /// <summary>
    /// The scanner the app runs with: real volumes, sizes remembered across runs.
    ///
    /// A single shared instance, following <c>ProcessRunner.Default</c> (G5). Sharing is not
    /// incidental here — the volume index is the entire cost of the fast path, and one scanner per
    /// provider would rebuild it three times over, making the MFT route slower than the walk it
    /// replaces.
    ///
    /// <para>Lazy, so that touching any static here builds nothing of this machine's: a scanner of
    /// another machine, a test's among them, never loads this account's size cache.</para>
    /// </summary>
    private static readonly Lazy<DirectoryScanner> Default = new(() => CreateDefault(UserEnvironment.Current));

    /// <summary>
    /// A scanner of the machine <paramref name="environment"/> describes: its volumes' tables, and
    /// sizes remembered where it keeps its application data, in the one cache of that machine
    /// (<see cref="ScanEstimateCache.For"/>).
    /// </summary>
    public static DirectoryScanner CreateDefault(IUserEnvironment environment, ScanTuner? tuning = null) =>
        new(environment.VolumeTables, ScanEstimateCache.For(environment), tuning: tuning);

    /// <summary>
    /// The scanner a provider or a planner given only <paramref name="environment"/> measures with.
    /// <see cref="Default"/> for this machine, which every <see cref="UserEnvironment"/> describes, so
    /// the one index stays shared. Any other environment is a machine of its caller's own, a test's
    /// fixture among them, and gets a scanner of that machine rather than the disks underneath it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This machine's, in a process that has closed <see cref="MachineScanFence"/>.
    /// </exception>
    public static DirectoryScanner For(IUserEnvironment environment)
    {
        if (environment is not UserEnvironment)
        {
            return CreateDefault(environment);
        }

        MachineScanFence.Pass();

        return Default.Value;
    }

    public async ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        ScanBounds bounds = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // §5.5's "re-opening the tool is instant": show last run's figure straight away, then
        // correct it. Only ever reported through progress — the returned result is always freshly
        // measured, because callers subtract it to report reclaimed space.
        if (Remembers(keep, bounds) && _estimates?.TryGet(path, bounds.KeepRoot) is { } remembered)
        {
            progress?.Report(remembered);
        }

        ScanRequest[] asked = [new(path, bounds)];
        var result = (await _routes.MeasureEachAsync(asked, [keep], RunningTotal.Over(asked, progress), ct).ConfigureAwait(false))[0][0];

        Remember(asked[0], keep, result);

        return result;
    }

    /// <summary>
    /// Both readings from whichever route takes the question: one built table, summed in memory once
    /// for each reading, or one walk. The whole is remembered as <see cref="MeasureAsync"/> remembers an
    /// unguarded figure, because it is one.
    /// </summary>
    public async ValueTask<AgedScan> MeasureAgedAsync(
        string path, MinimumAge age, IProgress<ScanSize>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ScanRequest[] asked = [new(path)];
        var keeps = AgedScan.Keeps(age);
        var readings = (await _routes.MeasureEachAsync(asked, keeps, RunningTotal.Over(asked, progress), ct).ConfigureAwait(false))[0];

        Remember(asked[0], keeps[0], readings[0]);

        return AgedScan.Of(readings);
    }

    /// <summary>
    /// Every path from the table where it can answer, and every path it cannot in one walk for each
    /// reason it could not on each set of disks, the sets at the same time. Each figure is remembered as <see cref="MeasureAsync"/> remembers it, and
    /// none is shown first: <paramref name="progress"/> counts what this measurement has read, and a
    /// figure from an earlier run is not that.
    /// </summary>
    public async ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default)
    {
        ScanRequest.ThrowIfAnyPathIsBlank(asked);

        using var remembering = _estimates?.Hold() ?? PassHold.Nothing;

        var results = await _routes.MeasureEachAsync(asked, [keep], RunningTotal.Over(asked, progress), ct).ConfigureAwait(false);

        for (var i = 0; i < asked.Count; i++)
        {
            Remember(asked[i], keep, results[i][0]);
        }

        return [.. results.Select(readings => readings[0])];
    }

    /// <summary>
    /// Whether a figure taken on these terms is the one the next run opens on. The remembered figure is
    /// for the whole path, so a run with the guard on neither reads it nor writes it. Reading it would
    /// flash a total this run is not going to reclaim, and writing it would leave the next run — where
    /// the user may have turned the guard off — opening on a number that is short by however much was
    /// held back. A figure with entries left out of it is short in the same way.
    /// </summary>
    private static bool Remembers(MinimumAge keep, ScanBounds bounds) => !keep.IsOn && !bounds.LeavesEntriesOut;

    private void Remember(ScanRequest asked, MinimumAge keep, ScanResult result)
    {
        if (Remembers(keep, asked.Bounds))
        {
            Remember(asked.Path, asked.Bounds, result);
        }
    }

    /// <summary>
    /// Straight to the walk, whatever the index holds, every path in one go as
    /// <see cref="MeasureManyAsync"/> walks the paths the table cannot answer. Each remembered estimate is
    /// still updated, because this is the freshest figure Deguffer has for the path, unless entries
    /// were left out of it, as <see cref="MeasureAsync"/> says.
    /// </summary>
    public async ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default)
    {
        ScanRequest.ThrowIfAnyPathIsBlank(asked);

        using var remembering = _estimates?.Hold() ?? PassHold.Nothing;
        // One set of answers for the walk's registration and its plan, so each drive is asked about once.
        var storages = _tuner.ForOneMeasurement();
        using var walking = _volumes.Walking([.. asked.Select(request => request.Path)], storages);

        var results = await _fallback
            .Because(FallbackReason.FreshReadingRequired)
            .MeasureEachAsync(asked, [MinimumAge.Off], RunningPaths.None, storages, ct)
            .ConfigureAwait(false);

        for (var i = 0; i < asked.Count; i++)
        {
            Remember(asked[i], MinimumAge.Off, results[i][0]);
        }

        return [.. results.Select(readings => readings[0])];
    }

    /// <summary>
    /// Keep <paramref name="result"/> as the figure the next run opens on, unless it measured
    /// nothing. A zero for a path the walk could not reach would open the next run on "this cache is
    /// empty", and the figure it replaces is still the last one anybody read. See
    /// <see cref="RootReach"/>.
    /// </summary>
    private void Remember(string path, ScanBounds bounds, ScanResult result)
    {
        if (result.WasReached)
        {
            _estimates?.Set(path, result.Size, bounds.KeepRoot);
        }
    }

    /// <summary>
    /// Ask the volume index what <paramref name="search"/> finds inside <paramref name="root"/>.
    ///
    /// The index is asked about the root's own subtree and nothing else, which is the consent model
    /// as well as the cost: the index knows every directory on the volume, and a cheap answer must
    /// not turn into permission to act on something the user never approved.
    /// </summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
        DirectorySearch search,
        string root,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        // Null under the walk-only setting, so a caller searches by walking as it does unelevated.
        // Answering from the table here would be the table's answer the user asked not to be given.
        if (_tuner.WalkOnly || !VolumePath.TryParse(root, out var volumeRoot))
        {
            return new((IReadOnlyList<string>?)null);
        }

        return new(FindAsync(search, volumeRoot, ct));
    }

    /// <summary>
    /// Under <see cref="ScanRoute.Auto"/>. A search waits for the volume's table, so a caller that can
    /// walk the root itself walks while it waits, as <see cref="MeasureAsync"/> does.
    /// </summary>
    public bool RacesTheWalk => _tuner.Route is ScanRoute.Auto;

    public void NeedIndex(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (VolumePath.TryParse(root, out var volumeRoot))
        {
            _volumes.Need(volumeRoot.DriveLetter);
        }
    }

    public IDisposable? Walking(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return _volumes.Walking([root]);
    }

    /// <summary>
    /// The index's answer for one root, as full paths in ordinal order, or null where it has none.
    ///
    /// <para>Not remembered between calls. The pass costs what the root's own subtree costs, and the
    /// discovery that asks remembers its whole answer for the planning pass, so a cache here would
    /// hold a second copy of it.</para>
    ///
    /// <para>Each path is the root as the caller gave it, then the names the table holds below it,
    /// which is how the walk spells what it finds. Ordinal, for the reason the walk sorts: a plan
    /// built from the answer reads the same on every run.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>?> FindAsync(DirectorySearch search, VolumePath root, CancellationToken ct)
    {
        // Not raised here: only the caller knows whether a walk is beside this wait, and it says so
        // with NeedIndex. Reading the route again here could disagree with the caller's reading.
        var built = await _volumes.Get(root.DriveLetter).WaitAsync(ct).ConfigureAwait(false);

        if (built.Index?.TryFindDirectories(root.Components, search, ct) is not { } found)
        {
            return null;
        }

        var prefix = root.FullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return [.. found.Select(components => prefix + string.Join(Path.DirectorySeparatorChar, components)).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Drop the volume indexes so the next pass reads the table afresh.
    ///
    /// Remembered estimates deliberately survive this. Invalidate runs at the *start* of a planning
    /// pass, and clearing them here would throw away the values that make the window populate
    /// instantly — the entire point of caching them. They need no explicit clearing in any case:
    /// every measurement that reached its path overwrites its own entry with the fresh figure, and
    /// one that did not leaves the last figure anybody read. See <see cref="Remember"/>.
    /// </summary>
    public void Invalidate()
    {
        _volumes.Invalidate();
        _tuner.Invalidate();
    }

    /// <summary>
    /// Keep the volume indexes for the pass, and the remembered estimates' changes in memory until it
    /// ends, when they are written once. The estimates themselves outlast the pass, as
    /// <see cref="Invalidate"/> says: only their writing waits for it.
    /// </summary>
    public PassHold Hold() => PassHold.Joined(_volumes.Hold(), _estimates?.Hold() ?? PassHold.Nothing);
}
