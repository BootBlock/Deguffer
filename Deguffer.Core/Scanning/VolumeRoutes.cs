using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Scanning;

/// <summary>
/// §5.5's choice between the two routes for each path a measurement asks about: the volume's table
/// where it can answer, raced against the walk while it is still being read, and the walk where it
/// cannot. One choice for every figure, so a scanner that counts on different terms
/// (<see cref="LinkCounting"/>) chooses its route exactly as <see cref="DirectoryScanner"/> does.
/// </summary>
/// <param name="volumes">The volume indexes of the pass, shared with every scanner that reads them.</param>
/// <param name="tuner">The route the user allows, and what each read runs with.</param>
/// <param name="walk">The walk a path the table does not answer is sent to.</param>
/// <param name="counting">What the table counts of a file with several names, as <paramref name="walk"/> counts it.</param>
/// <param name="indexOf">
/// The build of a volume's index this measurement reads, or null where it reads none and walks:
/// <see cref="MftVolumeIndexCache.Get"/>, which starts the build, or
/// <see cref="MftVolumeIndexCache.Started"/>, which reads only a build something else started.
/// </param>
internal sealed class VolumeRoutes(
    MftVolumeIndexCache volumes,
    ScanTuner tuner,
    IWalkRoute walk,
    LinkCounting counting,
    Func<char, Task<MftVolumeIndexCache.Built>?> indexOf)
{
    /// <summary>
    /// Each of <paramref name="asked"/> by the route that can answer it, in the order asked, read once
    /// for each of <paramref name="keeps"/> in their order.
    ///
    /// <para>A path the index cannot answer for is not the same as an empty one. The tree changed under
    /// the index, or the path runs through a link, or something below it does not establish its own
    /// size — so it is walked rather than reported as zero, which would render as "this cache is
    /// already clear" and quietly hide gigabytes. Every path of one set of disks walked for the same
    /// reason shares one walk.</para>
    ///
    /// <para><b>Disks at once, volumes on one disk in turn</b> (#470). The paths on each set of physical
    /// disks are measured at the same time as those on every other set (<see cref="FolderPipelines"/>,
    /// <see cref="PipelinesAtOnce"/>), so paths on two disks cost the longer of the two measurements,
    /// not their sum. Within a set, each volume's table is raced or read in turn, then the set's walks
    /// run, because starting a volume's table read is what starts it reading the disk.</para>
    /// </summary>
    /// <param name="running">
    /// Where each path's first reading goes, from whichever route gives it: the table's answer once it
    /// is read, and a walk's subtotals as they rise.
    /// </param>
    public async Task<ScanResult[][]> MeasureEachAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge[] keeps,
        RunningPaths running,
        CancellationToken ct)
    {
        var results = new ScanResult[asked.Count][];

        // Every question this measurement asks about what a path is on goes through these answers:
        // its pipelines, its walks' registration with the builds, and its walks' plans (#1065).
        var storages = tuner.ForOneMeasurement();

        // Asks a device only where the paths are on more than one volume, which a measurement of one
        // path never is. Each volume's first table read asks the same cache for its tuning anyway.
        var pipelines = FolderPipelines.Of([.. asked.Select(question => question.Path)], storages.Of);

        // Each pipeline writes the slots of its own paths, which no other pipeline writes. One of
        // several is started on a worker thread of its own, because a volume's table is opened on the
        // thread that first asks for it, and the next pipeline must not wait for that open to start.
        // A lone pipeline runs here, as every measurement ran before.
        await PipelinesAtOnce.RunAsync(
            pipelines,
            (pipeline, stop) => pipelines.Count == 1
                ? MeasurePipelineAsync(pipeline, asked, keeps, running, storages, results, stop)
                : WorkerThreads.Shared.RunAsync(() => MeasurePipelineAsync(pipeline, asked, keeps, running, storages, results, stop), stop).Unwrap(),
            ct).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// One pipeline's volumes, one after another: each volume's table raced or read for every path on
    /// it, then one walk for each reason a path of the pipeline was left to the walk.
    /// </summary>
    private async Task MeasurePipelineAsync(
        IReadOnlyList<VolumeFolders> pipeline,
        IReadOnlyList<ScanRequest> asked,
        MinimumAge[] keeps,
        RunningPaths running,
        PathStorages storages,
        ScanResult[][] results,
        CancellationToken ct)
    {
        var walks = new Dictionary<FallbackReason, List<int>>();

        void Walk(FallbackReason reason, int index)
        {
            if (!walks.TryGetValue(reason, out var indexes))
            {
                walks[reason] = indexes = [];
            }

            indexes.Add(index);
        }

        foreach (var volume in pipeline)
        {
            ct.ThrowIfCancellationRequested();

            if (volume.DriveLetter is not { } driveLetter || tuner.WalkOnly)
            {
                var reason = volume.DriveLetter is null ? FallbackReason.VolumeNotAddressable : FallbackReason.WalkChosen;

                foreach (var folder in volume.Folders)
                {
                    Walk(reason, folder.Index);
                }

                continue;
            }

            // No build to read, so nothing was fallen back from: the walk is the route chosen.
            if (indexOf(driveLetter) is not { } building)
            {
                foreach (var folder in volume.Folders)
                {
                    Walk(FallbackReason.None, folder.Index);
                }

                continue;
            }

            List<(int Index, Asked Asked)> questions =
            [
                .. volume.Folders.Select(folder => (folder.Index, new Asked(asked[folder.Index].Path, folder.Volume!.Value, keeps, asked[folder.Index].Bounds))),
            ];

            // Raced only while the table is still being read, and only where the user has not asked for
            // the table to be waited for. A table already read answers in a lookup, and one that could not
            // be read has a reason the walk has to carry, which a race would let a quick walk hide.
            var answered = !building.IsCompleted && tuner.Route is ScanRoute.Auto
                ? await RaceAsync(asked, questions, keeps, driveLetter, building, running, storages, ct).ConfigureAwait(false)
                : FromIndex((await WaitAloneAsync(driveLetter, building, ct).ConfigureAwait(false)).Index, questions);

            // The paths the table answered, reported together once its answers are in rather than one at a time.
            var finished = new List<int>(questions.Count);

            for (var j = 0; j < questions.Count; j++)
            {
                if (answered[j] is { } answer)
                {
                    finished.Add(questions[j].Index);
                    results[questions[j].Index] = answer;
                }
                else
                {
                    // Complete by now: the table either answered the other paths or was read without
                    // answering this one.
                    Walk(Declined(building.Result), questions[j].Index);
                }
            }

            running.Report(finished, i => results[i][0].Size);
        }

        foreach (var (reason, indexes) in walks)
        {
            using var walking = volumes.Walking([.. indexes.Select(i => asked[i].Path)], storages);

            var walked = await walk
                .Because(reason)
                .MeasureEachAsync([.. indexes.Select(i => asked[i])], keeps, running.Of(indexes), storages, ct)
                .ConfigureAwait(false);

            for (var j = 0; j < indexes.Count; j++)
            {
                results[indexes[j]] = walked[j];
            }
        }

        Diagnostics.IndexBuildLog.Answered(pipeline, results);
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
        char driveLetter,
        Task<MftVolumeIndexCache.Built> building,
        RunningPaths running,
        PathStorages storages,
        CancellationToken ct)
    {
        var raced = await RouteRace.FirstAsync<IReadOnlyList<ScanResult[]?>>(
            async stop => Answered(FromIndex((await building.WaitAsync(stop).ConfigureAwait(false)).Index, questions)),
            async stop =>
            {
                using var walking = volumes.Walking([.. questions.Select(question => question.Asked.Path)], storages);

                return await walk
                    .Because(FallbackReason.WalkAnsweredFirst)
                    .MeasureEachAsync(
                        [.. questions.Select(question => asked[question.Index])],
                        keeps,
                        running.Of([.. questions.Select(question => question.Index)]),
                        storages,
                        stop)
                    .ConfigureAwait(false);
            },
            walked => walked.All(readings => readings![0].WasReached),
            () => volumes.Need(driveLetter),
            ct).ConfigureAwait(false);

        return raced.Outcome switch
        {
            // The walk was stamped as the quicker route before anyone knew the table would answer none
            // of the paths. A file is a direct read, which carries no reason on either route, and a walk
            // that carries no reason of its own (SoleLinkWalk) is given none.
            RaceOutcome.TableDeclined => [.. raced.Answer.Select(readings => readings![0].Fallback is FallbackReason.WalkAnsweredFirst
                ? [.. readings.Select(walked => walked with { Fallback = Declined(building.Result) })]
                : readings)],
            _ => raced.Answer,
        };
    }

    /// <summary>
    /// Wait for the index of <paramref name="driveLetter"/> with no walk beside it, so its build is no
    /// longer speculative. A build started under Auto reads at low priority, and the route can change
    /// between the question that started it and this one.
    /// </summary>
    private Task<MftVolumeIndexCache.Built> WaitAloneAsync(char driveLetter, Task<MftVolumeIndexCache.Built> building, CancellationToken ct)
    {
        if (!building.IsCompleted)
        {
            volumes.Need(driveLetter);
        }

        return building.WaitAsync(ct);
    }

    /// <summary>
    /// Null where the table answered none of the paths, so the race takes the walk's answers rather
    /// than walking every path a second time.
    /// </summary>
    private static IReadOnlyList<ScanResult[]?>? Answered(IReadOnlyList<ScanResult[]?> fromTable) =>
        fromTable.Any(answer => answer is not null) ? fromTable : null;

    /// <summary>What the index says about each of <paramref name="questions"/>. See the overload below.</summary>
    private IReadOnlyList<ScanResult[]?> FromIndex(MftVolumeIndex? index, IReadOnlyList<(int Index, Asked Asked)> questions) =>
        [.. questions.Select(question => FromIndex(index, question.Asked))];

    /// <summary>
    /// What the index says about the path <paramref name="asked"/> names, one reading for each of its
    /// guards, or null where there is no index, or it cannot answer for the path or place an entry the
    /// bounds leave out.
    ///
    /// <para>All or nothing, from one pass over the path's subtree for every guard: one reading the
    /// table declines sends every reading to the walk, which answers them all in the one pass it would
    /// have made for the first.</para>
    /// </summary>
    private ScanResult[]? FromIndex(MftVolumeIndex? index, Asked asked)
    {
        var volume = asked.Volume;

        if (index is null
            || LeftOutNodes(index, asked) is not { } leftOut
            || index.TryMeasure(volume.Components, asked.Keeps, asked.Bounds.KeepRoot, leftOut, counting) is not { } measured)
        {
            return null;
        }

        // One age and one set of stores for every reading: a guard changes what is counted, never
        // what the directory holds.
        var age = OneLevelAge.ToUtc(index.AgeOf(volume.Components));
        IReadOnlyList<string> stores = [.. measured.MailStores.Select(s => PathOf(volume.DriveLetter, s)).Order(StringComparer.OrdinalIgnoreCase)];

        return [.. measured.Readings.Select(reading => ScanResult.Fast(reading.Size, reading.WithheldRecent) with
        {
            MailStores = stores,
            LastWritten = age,
        })];
    }

    /// <summary>
    /// The table nodes of the entries the bounds leave out below the path, none where they leave nothing
    /// out, or null where the index cannot place one.
    /// </summary>
    private static IReadOnlySet<uint>? LeftOutNodes(MftVolumeIndex index, Asked asked) =>
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
    /// instead, as <see cref="DirectoryScanner.TryFindDirectoriesAsync"/> says, because that is how the
    /// walk spells it.
    /// </summary>
    private static string PathOf(char driveLetter, IReadOnlyList<string> components) =>
        driveLetter + @":\" + string.Join(Path.DirectorySeparatorChar, components);
}
