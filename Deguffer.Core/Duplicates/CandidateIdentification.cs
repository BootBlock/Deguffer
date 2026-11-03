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
/// </summary>
internal sealed class CandidateIdentification
{
    private readonly FileInformation _files;
    private readonly MatchCriteria _criteria;
    private readonly VolumeMediaCache _media;
    private readonly Dictionary<FoundFile, IdentifiedFile?> _identified = [];
    private readonly Dictionary<FileIdentity, IReadOnlyList<string>> _names = [];

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
            await IdentifyAsync(found.Where(file => CandidateGrouping.TreeMinuteOf(file) is null), progress, ct).ConfigureAwait(false);
        }

        var byTheTree = CandidateGrouping.ByTheTree(found, _criteria, file =>
            CandidateGrouping.TreeMinuteOf(file) ?? (_identified[file] is { } identified
                ? CandidateGrouping.MinuteOf(identified.Description.Modified)
                : null));

        IReadOnlyList<FoundFile> grouped = [.. byTheTree.SelectMany(group => group)];

        await IdentifyAsync(grouped, progress, ct).ConfigureAwait(false);

        return CandidateGrouping.ByTheFiles([.. grouped.Select(file => _identified[file]).OfType<IdentifiedFile>()], _criteria, NamesOf);
    }

    /// <summary>
    /// Every file of <paramref name="files"/> not yet identified, described by Windows now, in lanes
    /// by disk, and remembered once every reader has finished, so each file is asked about once.
    /// </summary>
    private async Task IdentifyAsync(IEnumerable<FoundFile> files, IProgress<DuplicateSearchProgress>? progress, CancellationToken ct)
    {
        IReadOnlyList<FoundFile> unasked = [.. files.Where(file => !_identified.ContainsKey(file)).Distinct()];

        if (unasked.Count == 0)
        {
            return;
        }

        var identified = new IdentifiedFile?[unasked.Count];
        var counter = new StageCounter(DuplicateSearchStage.Identifying, unasked.Count, progress);
        var lanes = ReadingLanes.Of(Enumerable.Range(0, unasked.Count), file => unasked[file].Volume, _media);

        await ReadingLane<int>.ReadAllAsync(
            lanes,
            file =>
            {
                var found = unasked[file];
                identified[file] = Decide(found, _files.Describe(found.Tree.PathOf(found.Node), found.Route));
                counter.Finished();
            },
            ct).ConfigureAwait(false);

        for (var file = 0; file < unasked.Count; file++)
        {
            _identified[unasked[file]] = identified[file];
        }
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

    /// <summary>
    /// Every name of a file with several, listed once a file. Where Windows will not list them, the
    /// paths the search reached the file by stand in, so the file is never shown with none.
    /// </summary>
    private IReadOnlyList<string> NamesOf(IdentifiedFile file)
    {
        var identity = file.Description.Identity;

        if (!_names.TryGetValue(identity, out var names))
        {
            _names[identity] = names = _files.NamesOf(file.Description.Path)
                ?? [.. _identified.Values
                    .Where(other => other?.Description.Identity == identity)
                    .Select(other => other!.Description.Path)
                    .Distinct(StringComparer.Ordinal)];
        }

        return names;
    }
}
