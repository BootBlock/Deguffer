using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The scanner §5.5 asks for: read the MFT, fall back to a bounded parallel walk where that is not
/// possible, cache across runs, and stream partial results rather than blocking.
///
/// Choosing between the two routes lives here and nowhere else. Providers, the planner and the
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

    /// <param name="tuning">
    /// The route the user allows and the values each read runs with, asked as each measurement
    /// starts. <see cref="ScanTuner.Shipped"/> where none is given.
    /// </param>
    public DirectoryScanner(
        IMftSourceFactory? sources = null,
        ScanEstimateCache? estimates = null,
        ParallelEnumerationScanner? fallback = null,
        ScanTuner? tuning = null)
    {
        _tuner = tuning ?? ScanTuner.Shipped;
        _volumes = new MftVolumeIndexCache(sources ?? VolumeMftSourceFactory.Default, _tuner);
        _estimates = estimates;
        _fallback = fallback ?? new ParallelEnumerationScanner(_tuner);
    }

    /// <summary>
    /// The scanner the app runs with: real volumes, sizes remembered across runs.
    ///
    /// A single shared instance, following <c>ProcessRunner.Default</c> (G5). Sharing is not
    /// incidental here — the volume index is the entire cost of the fast path, and one scanner per
    /// provider would rebuild it three times over, making the MFT route slower than the walk it
    /// replaces.
    /// </summary>
    public static DirectoryScanner Default { get; } = CreateDefault(UserEnvironment.Current);

    public static DirectoryScanner CreateDefault(IUserEnvironment environment, ScanTuner? tuning = null) =>
        new(VolumeMftSourceFactory.Default, new ScanEstimateCache(environment), tuning: tuning);

    public async ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // §5.5's "re-opening the tool is instant": show last run's figure straight away, then
        // correct it. Only ever reported through progress — the returned result is always freshly
        // measured, because callers subtract it to report reclaimed space.
        //
        // The remembered figure is for the whole path, so a run with the guard on neither reads it
        // nor writes it. Reading it would flash a total this run is not going to reclaim, and
        // writing it would leave the next run — where the user may have turned the guard off —
        // opening on a number that is short by however much was held back.
        var remember = !keep.IsOn;

        if (remember && _estimates?.TryGet(path) is { } remembered)
        {
            progress?.Report(remembered);
        }

        var result = await MeasureFreshAsync(path, keep, progress, ct).ConfigureAwait(false);

        if (remember)
        {
            Remember(path, result);
        }

        return result;
    }

    /// <summary>
    /// Straight to the walk, whatever the index holds. The remembered estimate is still updated,
    /// because this is the freshest figure Deguffer has for the path.
    /// </summary>
    public async ValueTask<ScanResult> MeasureFromDiskAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var result = await _fallback
            .Because(FallbackReason.FreshReadingRequired)
            .MeasureAsync(path, MinimumAge.Off, progress: null, ct)
            .ConfigureAwait(false);

        Remember(path, result);
        return result;
    }

    /// <summary>
    /// Keep <paramref name="result"/> as the figure the next run opens on, unless it measured
    /// nothing. A zero for a path the walk could not reach would open the next run on "this cache is
    /// empty", and the figure it replaces is still the last one anybody read. See
    /// <see cref="RootReach"/>.
    /// </summary>
    private void Remember(string path, ScanResult result)
    {
        if (result.WasReached)
        {
            _estimates?.Set(path, result.Size);
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
        var built = await _volumes.Get(root.DriveLetter).WaitAsync(ct).ConfigureAwait(false);

        if (built.Index?.TryFindDirectories(root.Components, search, ct) is not { } found)
        {
            return null;
        }

        var prefix = root.FullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return [.. found.Select(components => prefix + string.Join(Path.DirectorySeparatorChar, components)).Order(StringComparer.Ordinal)];
    }

    private async ValueTask<ScanResult> MeasureFreshAsync(
        string path,
        MinimumAge keep,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        if (!VolumePath.TryParse(path, out var volumePath))
        {
            return await _fallback
                .Because(FallbackReason.VolumeNotAddressable)
                .MeasureAsync(path, keep, progress, ct)
                .ConfigureAwait(false);
        }

        if (_tuner.WalkOnly)
        {
            return await _fallback.Because(FallbackReason.WalkChosen).MeasureAsync(path, keep, progress, ct).ConfigureAwait(false);
        }

        var building = _volumes.Get(volumePath.DriveLetter);

        // Raced only while the table is still being read, and only where the user has not asked for
        // the table to be waited for. A table already read answers in a lookup, and one that could not
        // be read has a reason the walk has to carry, which a race would let a quick walk hide.
        if (!building.IsCompleted && _tuner.Route is ScanRoute.Auto)
        {
            return await RaceAsync(path, volumePath, building, keep, progress, ct).ConfigureAwait(false);
        }

        var built = await building.WaitAsync(ct).ConfigureAwait(false);

        // A path the index cannot answer for is not the same as an empty one. The tree changed
        // under the index, or the path runs through a link, or something below it does not
        // establish its own size — so ask the slow path rather than reporting zero, which would
        // render as "this cache is already clear" and quietly hide gigabytes.
        if (FromIndex(built.Index, volumePath, keep) is { } indexed)
        {
            progress?.Report(indexed.Size);
            return indexed;
        }

        return await _fallback.Because(Declined(built)).MeasureAsync(path, keep, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Walk <paramref name="path"/> while the table is read, and take whichever answers first. See
    /// <see cref="ScanRoute.Auto"/>.
    /// </summary>
    private async ValueTask<ScanResult> RaceAsync(
        string path,
        VolumePath volumePath,
        Task<MftVolumeIndexCache.Built> building,
        MinimumAge keep,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        var raced = await RouteRace.FirstAsync(
            async stop => FromIndex((await building.WaitAsync(stop).ConfigureAwait(false)).Index, volumePath, keep),
            stop => _fallback.Because(FallbackReason.WalkAnsweredFirst).MeasureAsync(path, keep, progress, stop).AsTask(),
            walked => walked.WasReached,
            ct).ConfigureAwait(false);

        switch (raced.Outcome)
        {
            case RaceOutcome.TableAnswered:
                // After the walk has stopped, so the last figure the caller is shown is this one.
                progress?.Report(raced.Answer.Size);
                return raced.Answer;

            // The walk was stamped as the quicker route before anyone knew the table would decline.
            // A file is a direct read, which carries no reason on either route.
            case RaceOutcome.TableDeclined when raced.Answer.Strategy is ScanStrategy.ParallelEnumeration:
                return raced.Answer with { Fallback = Declined(building.Result) };

            default:
                return raced.Answer;
        }
    }

    /// <summary>
    /// What the index says about <paramref name="volumePath"/>, or null where there is no index or it
    /// cannot answer for the path.
    /// </summary>
    private static ScanResult? FromIndex(MftVolumeIndex? index, VolumePath volumePath, MinimumAge keep)
    {
        if (index is null || index.TryMeasure(volumePath.Components, keep, out var withheldRecent, out var stores) is not { } size)
        {
            return null;
        }

        return ScanResult.Fast(size, withheldRecent) with
        {
            MailStores = [.. stores.Select(s => PathOf(volumePath.DriveLetter, s)).Order(StringComparer.OrdinalIgnoreCase)],
        };
    }

    /// <summary>Why the walk answered a path the table did not.</summary>
    private static FallbackReason Declined(MftVolumeIndexCache.Built built) =>
        built.Reason == FallbackReason.None ? FallbackReason.MasterFileTableIncomplete : built.Reason;

    /// <summary>
    /// A mail store's path, which the index rebuilt as components below a volume root, in the display
    /// form the walk reports. A directory found by name is spelled from the root the caller gave
    /// instead, as <see cref="FindAsync"/> says, because that is how the walk spells it.
    /// </summary>
    private static string PathOf(char driveLetter, IReadOnlyList<string> components) =>
        driveLetter + @":\" + string.Join(Path.DirectorySeparatorChar, components);

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
}
