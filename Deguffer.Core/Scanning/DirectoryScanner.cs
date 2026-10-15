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

        var asked = new ScanRequest(path, bounds);
        var result = (await MeasureFreshAsync([asked], [keep], progress, ct).ConfigureAwait(false))[0][0];

        Remember(asked, keep, result);

        return result;
    }

    /// <summary>
    /// Both readings from whichever route takes the question: one built table, summed in memory once
    /// for each reading, or one walk. The whole is remembered as <see cref="MeasureAsync"/> remembers an
    /// unguarded figure, because it is one.
    /// </summary>
    public async ValueTask<AgedScan> MeasureAgedAsync(string path, MinimumAge age, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var asked = new ScanRequest(path);
        var keeps = AgedScan.Keeps(age);
        var readings = (await MeasureFreshAsync([asked], keeps, progress: null, ct).ConfigureAwait(false))[0];

        Remember(asked, keeps[0], readings[0]);

        return AgedScan.Of(readings);
    }

    /// <summary>
    /// Every path from the table where it can answer, and every path it cannot in one walk for each
    /// reason it could not. Each figure is remembered as <see cref="MeasureAsync"/> remembers it, and
    /// none is shown first, because nothing shows a list's progress.
    /// </summary>
    public async ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep = default,
        CancellationToken ct = default)
    {
        ThrowIfAnyPathIsBlank(asked);

        var results = await MeasureFreshAsync(asked, [keep], progress: null, ct).ConfigureAwait(false);

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
        ThrowIfAnyPathIsBlank(asked);

        var results = await _fallback
            .Because(FallbackReason.FreshReadingRequired)
            .MeasureManyAsync(asked, MinimumAge.Off, ct)
            .ConfigureAwait(false);

        for (var i = 0; i < asked.Count; i++)
        {
            Remember(asked[i], MinimumAge.Off, results[i]);
        }

        return results;
    }

    private static void ThrowIfAnyPathIsBlank(IReadOnlyList<ScanRequest> asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        foreach (var question in asked)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(question.Path, nameof(asked));
        }
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

    /// <summary>
    /// Each of <paramref name="asked"/> by the route that can answer it, in the order asked, read once
    /// for each of <paramref name="keeps"/> in their order.
    ///
    /// <para>A path the index cannot answer for is not the same as an empty one. The tree changed under
    /// the index, or the path runs through a link, or something below it does not establish its own
    /// size — so it is walked rather than reported as zero, which would render as "this cache is
    /// already clear" and quietly hide gigabytes. Every path walked for the same reason shares one walk.
    /// </para>
    /// </summary>
    /// <param name="progress">
    /// The running figure of a measurement of one path under one guard, which is the only one that
    /// reports any.
    /// </param>
    private async ValueTask<ScanResult[][]> MeasureFreshAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge[] keeps,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        var results = new ScanResult[asked.Count][];
        var walks = new Dictionary<FallbackReason, List<int>>();
        var onVolumes = new Dictionary<char, List<(int Index, Asked Asked)>>();

        void Walk(FallbackReason reason, int index)
        {
            if (!walks.TryGetValue(reason, out var indexes))
            {
                walks[reason] = indexes = [];
            }

            indexes.Add(index);
        }

        for (var i = 0; i < asked.Count; i++)
        {
            var (path, bounds) = asked[i];

            if (!VolumePath.TryParse(path, out var volumePath))
            {
                Walk(FallbackReason.VolumeNotAddressable, i);
            }
            else if (_tuner.WalkOnly)
            {
                Walk(FallbackReason.WalkChosen, i);
            }
            else
            {
                if (!onVolumes.TryGetValue(volumePath.DriveLetter, out var questions))
                {
                    onVolumes[volumePath.DriveLetter] = questions = [];
                }

                questions.Add((i, new Asked(path, volumePath, keeps, bounds)));
            }
        }

        foreach (var (driveLetter, questions) in onVolumes)
        {
            var building = _volumes.Get(driveLetter);

            // Raced only while the table is still being read, and only where the user has not asked for
            // the table to be waited for. A table already read answers in a lookup, and one that could not
            // be read has a reason the walk has to carry, which a race would let a quick walk hide.
            var answered = !building.IsCompleted && _tuner.Route is ScanRoute.Auto
                ? await RaceAsync(asked, questions, keeps, building, progress, ct).ConfigureAwait(false)
                : FromIndex((await building.WaitAsync(ct).ConfigureAwait(false)).Index, questions);

            for (var j = 0; j < questions.Count; j++)
            {
                if (answered[j] is { } answer)
                {
                    progress?.Report(answer[0].Size);
                    results[questions[j].Index] = answer;
                }
                else
                {
                    // Complete by now: the table either answered the other paths or was read without
                    // answering this one.
                    Walk(Declined(building.Result), questions[j].Index);
                }
            }
        }

        foreach (var (reason, indexes) in walks)
        {
            var walked = await _fallback
                .Because(reason)
                .MeasureEachAsync([.. indexes.Select(i => asked[i])], keeps, progress, ct)
                .ConfigureAwait(false);

            for (var j = 0; j < indexes.Count; j++)
            {
                results[indexes[j]] = walked[j];
            }
        }

        return results;
    }

    /// <summary>
    /// One measurement's question, as each route is asked it: one path, read once for each guard in
    /// <see cref="Keeps"/>.
    /// </summary>
    private readonly record struct Asked(string Path, VolumePath Volume, MinimumAge[] Keeps, ScanBounds Bounds);

    /// <summary>
    /// Walk the paths of one volume while its table is read, and take whichever answers first. See
    /// <see cref="ScanRoute.Auto"/>. Null for each path left for the walk: one the table declined
    /// where it answered others, which is walked again with the reason it declined.
    ///
    /// <para>The walk answers only where it reached every path. One it could not reach has said nothing
    /// the table cannot say better, so the table is waited for, as <see cref="RouteRace"/> says. Every
    /// reading of a path shares one walk's reach, so its first speaks for all of them.</para>
    /// </summary>
    private async ValueTask<IReadOnlyList<ScanResult[]?>> RaceAsync(
        IReadOnlyList<ScanRequest> asked,
        IReadOnlyList<(int Index, Asked Asked)> questions,
        MinimumAge[] keeps,
        Task<MftVolumeIndexCache.Built> building,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        var raced = await RouteRace.FirstAsync<IReadOnlyList<ScanResult[]?>>(
            async stop => Answered(FromIndex((await building.WaitAsync(stop).ConfigureAwait(false)).Index, questions)),
            async stop => await _fallback
                .Because(FallbackReason.WalkAnsweredFirst)
                .MeasureEachAsync([.. questions.Select(question => asked[question.Index])], keeps, progress, stop)
                .ConfigureAwait(false),
            walked => walked.All(readings => readings![0].WasReached),
            ct).ConfigureAwait(false);

        return raced.Outcome switch
        {
            // The walk was stamped as the quicker route before anyone knew the table would answer none
            // of the paths. A file is a direct read, which carries no reason on either route.
            RaceOutcome.TableDeclined => [.. raced.Answer.Select(readings => readings![0].Strategy is ScanStrategy.ParallelEnumeration
                ? [.. readings.Select(walked => walked with { Fallback = Declined(building.Result) })]
                : readings)],
            _ => raced.Answer,
        };
    }

    /// <summary>
    /// Null where the table answered none of the paths, so the race takes the walk's answers rather
    /// than walking every path a second time.
    /// </summary>
    private static IReadOnlyList<ScanResult[]?>? Answered(IReadOnlyList<ScanResult[]?> fromTable) =>
        fromTable.Any(answer => answer is not null) ? fromTable : null;

    /// <summary>What the index says about each of <paramref name="questions"/>. See the overload below.</summary>
    private static IReadOnlyList<ScanResult[]?> FromIndex(MftVolumeIndex? index, IReadOnlyList<(int Index, Asked Asked)> questions) =>
        [.. questions.Select(question => FromIndex(index, question.Asked))];

    /// <summary>
    /// What the index says about the path <paramref name="asked"/> names, one reading for each of its
    /// guards, or null where there is no index, or it cannot answer for the path or place an entry the
    /// bounds leave out.
    ///
    /// <para>All or nothing: one reading the table declines sends every reading to the walk, which
    /// answers them all in the one pass it would have made for the first.</para>
    /// </summary>
    private static ScanResult[]? FromIndex(MftVolumeIndex? index, Asked asked)
    {
        var volume = asked.Volume;

        if (index is null || LeftOutRecords(index, asked) is not { } leftOut)
        {
            return null;
        }

        var readings = new ScanResult[asked.Keeps.Length];

        for (var i = 0; i < readings.Length; i++)
        {
            if (index.TryMeasure(volume.Components, asked.Keeps[i], asked.Bounds.KeepRoot, leftOut, out var withheldRecent, out var stores) is not { } size)
            {
                return null;
            }

            readings[i] = ScanResult.Fast(size, withheldRecent) with
            {
                MailStores = [.. stores.Select(s => PathOf(volume.DriveLetter, s)).Order(StringComparer.OrdinalIgnoreCase)],
            };
        }

        return readings;
    }

    /// <summary>
    /// The records of the entries the bounds leave out below the path, none where they leave nothing
    /// out, or null where the index cannot place one.
    /// </summary>
    private static IReadOnlySet<uint>? LeftOutRecords(MftVolumeIndex index, Asked asked) =>
        LeftOutEntries.Of(asked.Path, asked.Bounds) is { } entries
            ? index.TryPlace(asked.Volume.Components, entries.Below, components => DescribeEntry(asked.Volume.DriveLetter, components))
            : MftVolumeIndex.NothingLeftOut;

    /// <summary>
    /// The entry at a path below the volume root, read from the disk for its file number, for an entry
    /// the table holds no name for. See <see cref="MftVolumeIndex.TryPlace"/>.
    /// </summary>
    private static FileReading DescribeEntry(char driveLetter, IReadOnlyList<string> components) =>
        FileInformation.Default.Describe(PathOf(driveLetter, components), IdentityRoute.Legacy);

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
