using System.Collections.Concurrent;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Duplicates;

/// <summary>Reads part of one file into a checksum, as <see cref="ContentReader.Read"/> does.</summary>
internal delegate ContentReading ReadContent(DuplicateCandidate file, ContentPart part, Checksum checksum, CancellationToken ct);

/// <summary>
/// Matches the candidates of a content search by their bytes, in stages (§7.4): the first and last
/// blocks of every file, then the whole of the larger files whose blocks matched. Each stage reads
/// only what the last left in a group with another file, because most files of one length differ in
/// their first or last block, and reading them in full would be most of a search's time.
///
/// <para><b>A file read whole at the first stage is done.</b> A file no longer than its first and last
/// blocks is read at once in full, so its group is confirmed as soon as its files are read.</para>
///
/// <para><b>Confirmed as each group finishes.</b> A group is matched again the moment its last file
/// is read, and a group of the last stage is passed on then, so results reach the page while the
/// search runs. Nothing is passed on once the search is stopped.</para>
///
/// <para><b>Read in lanes, one a disk</b> (<see cref="ReadingLanes"/>), each bounded, all at once.
/// A file that could not be read, or was not the identified file by the time it was, is left out
/// and counted; an error never makes two files the same.</para>
/// </summary>
internal sealed class ContentMatching
{
    /// <summary>How often a stage says how far it has got, which is often enough to watch and rare enough not to flood the page.</summary>
    private const long ReportEveryMilliseconds = 100;

    private readonly ReadContent _read;
    private readonly VolumeMediaCache _media;
    private readonly Lock _confirming = new();

    private int _gone;
    private int _unidentified;
    private int _onlyInTheCloud;
    private int _readFailed;
    private int _changed;

    public ContentMatching(ReadContent read, VolumeMediaCache media)
    {
        _read = read;
        _media = media;
    }

    /// <summary>What reading left out, to add to what finding the candidates left out.</summary>
    public LeftOutFiles LeftOut => new(
        Links: 0, Empty: 0, UnknownLength: 0, _onlyInTheCloud, _gone, _unidentified, _readFailed, _changed);

    /// <param name="groups">The candidates, each group sharing a length.</param>
    /// <param name="confirmed">Given each group as it is confirmed, from any thread, one at a time.</param>
    /// <exception cref="OperationCanceledException">The search was stopped; the groups already given stand.</exception>
    public async Task MatchAsync(
        IReadOnlyList<CandidateGroup> groups,
        MatchCriteria criteria,
        ChecksumAlgorithm algorithm,
        Action<DuplicateGroup> confirmed,
        IProgress<DuplicateSearchProgress>? progress,
        CancellationToken ct)
    {
        // The groups that could free the most go first, so the results worth most arrive first.
        IReadOnlyList<IReadOnlyList<DuplicateCandidate>> byTheirEnds =
            [.. groups.OrderByDescending(group => group.Files[0].Length * (group.Files.Count - 1)).Select(group => group.Files)];

        var unsettled = await StageAsync(
            DuplicateSearchStage.FirstAndLastBlocks, ContentPart.Ends, byTheirEnds, criteria, algorithm, confirmed, progress, ct)
            .ConfigureAwait(false);

        await StageAsync(DuplicateSearchStage.FullContent, ContentPart.Whole, unsettled, criteria, algorithm, confirmed, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <paramref name="part"/> of every file in <paramref name="groups"/>, confirms each group
    /// that the reading settles, and answers the groups the next stage must read further.
    /// </summary>
    private async Task<IReadOnlyList<IReadOnlyList<DuplicateCandidate>>> StageAsync(
        DuplicateSearchStage stage,
        ContentPart part,
        IReadOnlyList<IReadOnlyList<DuplicateCandidate>> groups,
        MatchCriteria criteria,
        ChecksumAlgorithm algorithm,
        Action<DuplicateGroup> confirmed,
        IProgress<DuplicateSearchProgress>? progress,
        CancellationToken ct)
    {
        var total = groups.Sum(group => group.Count);
        var unread = groups.Select(group => group.Count).ToArray();
        var checksums = groups.Select(group => new ContentChecksum?[group.Count]).ToArray();
        List<IReadOnlyList<DuplicateCandidate>> further = [];
        ConcurrentBag<Checksum> idle = [];
        var done = 0;
        var reported = Environment.TickCount64;

        progress?.Report(new DuplicateSearchProgress(stage, 0, total));

        var lanes = ReadingLanes.Of(
            groups.SelectMany((group, g) => group.Select((file, f) => (Group: g, File: f))),
            item => groups[item.Group][item.File].Volume,
            _media);

        try
        {
            await Task.WhenAll(lanes.Select(lane => Parallel.ForEachAsync(
                lane.Files,
                new ParallelOptions { MaxDegreeOfParallelism = lane.Readers, CancellationToken = ct },
                (item, token) =>
                {
                    checksums[item.Group][item.File] = Read(groups[item.Group][item.File], part, algorithm, idle, token);

                    var finished = Interlocked.Increment(ref done);
                    var now = Environment.TickCount64;
                    var last = Volatile.Read(ref reported);

                    if (finished == total || (now - last >= ReportEveryMilliseconds && Interlocked.CompareExchange(ref reported, now, last) == last))
                    {
                        progress?.Report(new DuplicateSearchProgress(stage, finished, total));
                    }

                    if (Interlocked.Decrement(ref unread[item.Group]) == 0)
                    {
                        Settle(groups[item.Group], checksums[item.Group], part, criteria, confirmed, further, ct);
                    }

                    return ValueTask.CompletedTask;
                }))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var checksum in idle)
            {
                checksum.Dispose();
            }
        }

        return further;
    }

    /// <summary>
    /// The checksum of one file's <paramref name="part"/>, or null where it was left out, through a
    /// running checksum no other reader holds at the time.
    /// </summary>
    private ContentChecksum? Read(DuplicateCandidate file, ContentPart part, ChecksumAlgorithm algorithm, ConcurrentBag<Checksum> idle, CancellationToken ct)
    {
        var checksum = idle.TryTake(out var free) ? free : Checksum.For(algorithm);

        try
        {
            var reading = _read(file, part, checksum, ct);

            switch (reading.Result)
            {
                case ContentReadResult.Read:
                    return reading.Checksum;

                case ContentReadResult.Gone:
                    Interlocked.Increment(ref _gone);
                    break;

                case ContentReadResult.Unidentified:
                    Interlocked.Increment(ref _unidentified);
                    break;

                case ContentReadResult.OnlyInTheCloud:
                    Interlocked.Increment(ref _onlyInTheCloud);
                    break;

                case ContentReadResult.ReadFailed:
                    Interlocked.Increment(ref _readFailed);
                    break;

                case ContentReadResult.Changed:
                    Interlocked.Increment(ref _changed);
                    break;
            }

            return null;
        }
        finally
        {
            idle.Add(checksum);
        }
    }

    /// <summary>
    /// Groups the files of one finished group by what was read, and confirms each match that read
    /// every byte or passes it on to be read in full. A file alone in its group has no match.
    /// </summary>
    private void Settle(
        IReadOnlyList<DuplicateCandidate> group,
        ContentChecksum?[] checksums,
        ContentPart part,
        MatchCriteria criteria,
        Action<DuplicateGroup> confirmed,
        List<IReadOnlyList<DuplicateCandidate>> further,
        CancellationToken ct)
    {
        var whole = ContentReader.IsWhole(part, group[0].Length);

        var matches = group
            .Select((file, i) => (File: file, Checksum: checksums[i]))
            .Where(read => read.Checksum is not null)
            .GroupBy(read => read.Checksum!.Value)
            .Where(match => match.Count() > 1);

        foreach (var match in matches)
        {
            IReadOnlyList<DuplicateCandidate> files = [.. match.Select(read => read.File)];

            lock (_confirming)
            {
                // Under the same lock as every other confirmation, so none is passed on after the
                // search was stopped, and the caller is never given two at once.
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (whole)
                {
                    confirmed(new DuplicateGroup(criteria, files[0].Length, match.Key, files));
                }
                else
                {
                    further.Add(files);
                }
            }
        }
    }
}
