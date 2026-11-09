using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// The second stage of a duplicate search (§7.4): identifies the files that share a group by what the
/// tree holds, and groups them again by what Windows says of each, so a file is one file however
/// many paths reached it, and the times compare to the tick.
///
/// <para><b>Only the files that may match are opened.</b> A drive holds millions of files and most
/// share nothing with another, so the tree's grouping decides which are identified. Where the time is
/// a criterion and the tree holds none for a file, that file is identified first and placed by the
/// minute Windows gives, so it can still join a file the tree did date.</para>
///
/// <para><b>What could not be identified is left out and counted</b>, a file Windows would not
/// describe apart from one that is gone, because a file taken for gone may be the only copy left.
/// A path that names a folder or a link by then is counted as gone, since the file the scan saw is
/// no longer there. A file empty when it is opened is left out as the walk would have left it, and
/// one that went online-only since the scan is left out of a content search.</para>
///
/// <para><b>Identified in lanes, one a disk</b> (<see cref="ReadingLanes"/>), as the content is
/// read: a search of a whole drive has hundreds of thousands of files that share a length, and each
/// is an open and several questions of the file system. A disk not known to be solid state is asked
/// by one reader, so its file records are never read out of order by several.</para>
///
/// <para><b>The names of a file with several are listed in the same lanes, as the last pass of this
/// stage</b>, because a source tree of hard links holds tens of thousands of such files, each name
/// costing a path built by the file system, and on one thread that was many seconds in which the
/// page stood still (#708). The pass comes after the grouping, so a file alone in its group costs
/// nothing. Each name is given from the top of the file's volume, asked once for each searched
/// location: a location's enumeration stays on one volume, and a file whose final path lies below
/// the location's is named through the location's mount point.</para>
/// </summary>
internal sealed class CandidateIdentification
{
    private readonly FileInformation _files;
    private readonly MatchCriteria _criteria;
    private readonly VolumeMediaCache _media;
    private readonly Dictionary<FoundFile, IdentifiedFile?> _identified = [];

    private int _empty;
    private int _onlyInTheCloud;
    private int _gone;
    private int _unidentified;

    /// <param name="media">The disks behind each volume, and their kind, so the files are identified in lanes by disk.</param>
    public CandidateIdentification(FileInformation files, MatchCriteria criteria, VolumeMediaCache media)
    {
        _files = files;
        _criteria = criteria;
        _media = media;
    }

    /// <summary>What this stage left out, to add to what the walk left out.</summary>
    public LeftOutFiles LeftOut => new(Links: 0, _empty, UnknownLength: 0, _onlyInTheCloud, _gone, _unidentified, ReadFailed: 0, Changed: 0);

    /// <param name="progress">Told how far each pass has got, as <see cref="DuplicateSearchStage.Identifying"/>.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<IReadOnlyList<CandidateGroup>> GroupAsync(
        IReadOnlyList<FoundFile> found,
        IProgress<DuplicateSearchProgress>? progress,
        CancellationToken ct)
    {
        // A file the tree holds no time for is identified before the tree's grouping, so it is placed
        // by the minute Windows gives and can still join a file the tree did date.
        if (_criteria.GroupsByTime())
        {
            await IdentifyAsync([.. found.Where(file => CandidateGrouping.TreeMinuteOf(file) is null)], progress, ct).ConfigureAwait(false);
        }

        var byTheTree = CandidateGrouping.ByTheTree(found, _criteria, file =>
            CandidateGrouping.TreeMinuteOf(file) ?? (_identified[file] is { } identified
                ? CandidateGrouping.MinuteOf(identified.Description.Modified)
                : null));

        IReadOnlyList<FoundFile> grouped = [.. byTheTree.SelectMany(group => group)];

        await IdentifyAsync(grouped, progress, ct).ConfigureAwait(false);

        var groups = CandidateGrouping.ByTheFiles(grouped.Select(file => _identified[file]).OfType<IdentifiedFile>(), _criteria);
        var names = await ListNamesAsync(CandidateGrouping.NeedingNames(groups), progress, ct).ConfigureAwait(false);

        return [.. groups.Select(group => CandidateGrouping.WithNames(group, file => names[file.Description.Identity]))];
    }

    /// <summary>
    /// Every file of <paramref name="files"/> not yet identified, described by Windows now, in lanes
    /// by disk, and remembered once every reader has finished, so each file is asked about once.
    /// </summary>
    private async Task IdentifyAsync(IReadOnlyList<FoundFile> files, IProgress<DuplicateSearchProgress>? progress, CancellationToken ct)
    {
        IReadOnlyList<FoundFile> unasked = _identified.Count == 0 ? files : [.. files.Where(file => !_identified.ContainsKey(file))];

        if (unasked.Count == 0)
        {
            return;
        }

        _identified.EnsureCapacity(_identified.Count + unasked.Count);

        var identified = new IdentifiedFile?[unasked.Count];

        await InLanesAsync(
            unasked,
            file =>
            {
                var found = unasked[file];
                identified[file] = Decide(found, _files.Describe(found.Tree.PathOf(found.Node), found.Route));
            },
            progress,
            ct).ConfigureAwait(false);

        for (var file = 0; file < unasked.Count; file++)
        {
            _identified[unasked[file]] = identified[file];
        }
    }

    /// <summary>
    /// Every name of each of <paramref name="linked"/>, by its identity, listed in lanes by disk. Where
    /// Windows will not list them, the paths the search reached the file by stand in, so the file is
    /// never shown with none.
    /// </summary>
    private async Task<Dictionary<FileIdentity, IReadOnlyList<string>>> ListNamesAsync(
        IReadOnlyList<IdentifiedFile> linked,
        IProgress<DuplicateSearchProgress>? progress,
        CancellationToken ct)
    {
        Dictionary<FileIdentity, IReadOnlyList<string>> names = [];

        if (linked.Count == 0)
        {
            return names;
        }

        var mountPoints = linked.Select(file => file.Found.Root).Distinct().ToDictionary(root => root, root => _files.MountPointOf(root.Folder));
        var listed = new IReadOnlyList<string>?[linked.Count];

        await InLanesAsync(
            [.. linked.Select(file => file.Found)],
            file => listed[file] = MountPointOf(linked[file], mountPoints) is { } mountPoint
                ? _files.NamesOf(linked[file].Description.Path, mountPoint)
                : null,
            progress,
            ct).ConfigureAwait(false);

        ILookup<FileIdentity, string>? reached = null;

        for (var file = 0; file < linked.Count; file++)
        {
            var identity = linked[file].Description.Identity;

            names[identity] = listed[file] ?? [.. (reached ??= PathsReached())[identity].Distinct(StringComparer.Ordinal)];
        }

        return names;
    }

    /// <summary>
    /// Where <paramref name="file"/>'s volume is mounted: as its location was answered, unless Windows
    /// described the file somewhere outside that location. A folder replaced by a link since the walk
    /// can lead to another volume, and a name given from the wrong top would be judged as a place the
    /// file is not (§7.4).
    /// </summary>
    private string? MountPointOf(IdentifiedFile file, Dictionary<FoundRoot, string?> ofLocations) =>
        LongPath.Contains(file.Found.Root.Folder, file.Description.Path, StringComparison.Ordinal)
            ? ofLocations[file.Found.Root]
            : _files.MountPointOf(file.Description.Path);

    private ILookup<FileIdentity, string> PathsReached() =>
        _identified.Values.OfType<IdentifiedFile>().ToLookup(file => file.Description.Identity, file => file.Description.Path);

    /// <summary>Calls <paramref name="work"/> with the index of each of <paramref name="files"/>, in lanes by disk, as one pass of this stage.</summary>
    private async Task InLanesAsync(
        IReadOnlyList<FoundFile> files,
        Action<int> work,
        IProgress<DuplicateSearchProgress>? progress,
        CancellationToken ct)
    {
        var measure = new StageMeasure(DuplicateSearchStage.Identifying, files.Count, progress, TimeProvider.System);
        var lanes = ReadingLanes.Of(files.Select(file => file.Volume), _media);

        await new LaneWorkers<int>(lanes.Readers, ct).RunAsync(
            Enumerable.Range(0, files.Count).Select(file => (lanes.LaneOf(files[file].Volume), file, Priority: 0L)),
            file =>
            {
                work(file);
                measure.Done(1);
            }).ConfigureAwait(false);

        measure.Finished();
    }

    /// <summary>The file as Windows described it, or null where it is left out and counted. Called from any reader's thread.</summary>
    private IdentifiedFile? Decide(FoundFile file, FileReading reading)
    {
        switch (reading.Result)
        {
            case FileReadingResult.Gone:
                Interlocked.Increment(ref _gone);
                return null;

            case FileReadingResult.Unreadable:
                Interlocked.Increment(ref _unidentified);
                return null;
        }

        var description = reading.Description!;

        if (!description.IsFile)
        {
            Interlocked.Increment(ref _gone);
            return null;
        }

        if (description.Length == 0)
        {
            Interlocked.Increment(ref _empty);
            return null;
        }

        if (_criteria.ReadsContent() && StorageAttributes.Of(description.Attributes) is FileStorage.CloudOnly)
        {
            Interlocked.Increment(ref _onlyInTheCloud);
            return null;
        }

        return new IdentifiedFile(file, description);
    }
}
