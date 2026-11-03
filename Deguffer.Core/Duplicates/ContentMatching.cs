using System.Collections.Concurrent;
using Deguffer.Core.Scanning;
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
/// <para><b>The stages overlap.</b> A group is matched again the moment its last file is read, and the
/// files whose blocks still match are queued to be read in full there and then, beside the blocks of
/// the groups still waiting. So a fast disk reads its full content while a slow one is still reading
/// blocks, a disk bound by opens is also kept busy by long reads, and a large group is confirmed
/// without waiting for every small file's blocks. A group of the last stage is passed on as it is
/// confirmed, so results reach the page while the search runs. Nothing is passed on once the search
/// is stopped.</para>
///
/// <para><b>The groups that could free the most first.</b> Each read is queued with the space its
/// group could free, so within a disk a group's full reads overtake the blocks of smaller groups, and
/// the results worth most arrive first.</para>
///
/// <para><b>Read in lanes, one a disk</b> (<see cref="ReadingLanes"/>), each bounded, all at once,
/// each on threads of its own (<see cref="LaneWorkers{T}"/>) rather than the pool's. A spinning disk
/// is read by one in both stages. A file that could not be read, or was not the identified file by
/// the time it was, is left out and counted; an error never makes two files the same.</para>
/// </summary>
internal sealed class ContentMatching
{
    private readonly ReadContent _read;
    private readonly VolumeMediaCache _media;
    private readonly TimeProvider _clock;
    private readonly Lock _confirming = new();

    private int _gone;
    private int _unidentified;
    private int _onlyInTheCloud;
    private int _readFailed;
    private int _changed;

    public ContentMatching(ReadContent read, VolumeMediaCache media)
        : this(read, media, TimeProvider.System)
    {
    }

    /// <param name="clock">What times the progress reports: the seam a test reports every change through.</param>
    internal ContentMatching(ReadContent read, VolumeMediaCache media, TimeProvider clock)
    {
        _read = read;
        _media = media;
        _clock = clock;
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
        var run = new Run(
            this,
            ReadingLanes.Of(groups.SelectMany(group => group.Files).Select(file => file.Volume), _media),
            new StageMeasure(DuplicateSearchStage.ComparingContent, groups.SelectMany(group => group.Files).Sum(file => ContentReader.MostToRead(file.Length)), progress, _clock),
            criteria,
            algorithm,
            confirmed,
            ct);

        await run.MatchAsync(groups).ConfigureAwait(false);
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

    /// <summary>The space a group of <paramref name="files"/> could free, which orders its reads.</summary>
    private static long Space(IReadOnlyList<DuplicateCandidate> files) => files[0].Length * (files.Count - 1);

    /// <summary>One group of files as one part of each is read, and what has been read of them so far.</summary>
    private sealed class Reading(IReadOnlyList<DuplicateCandidate> files, ContentPart part)
    {
        public IReadOnlyList<DuplicateCandidate> Files { get; } = files;

        public ContentPart Part { get; } = part;

        public ContentChecksum?[] Checksums { get; } = new ContentChecksum?[files.Count];

        public int Unread = files.Count;
    }

    /// <summary>One file of a <see cref="Reading"/>, by its place there.</summary>
    private readonly record struct FileRead(Reading Group, int File);

    /// <summary>One call of <see cref="MatchAsync"/>: its lanes, its measure, and the running checksums its readers share.</summary>
    private sealed class Run(
        ContentMatching matching,
        ReadingLanes lanes,
        StageMeasure measure,
        MatchCriteria criteria,
        ChecksumAlgorithm algorithm,
        Action<DuplicateGroup> confirmed,
        CancellationToken ct)
    {
        private readonly LaneWorkers<FileRead> _workers = new(lanes.Readers, ct);
        private readonly ConcurrentBag<Checksum> _idle = [];

        public async Task MatchAsync(IReadOnlyList<CandidateGroup> groups)
        {
            try
            {
                await _workers.RunAsync(groups.SelectMany(group => Reads(new Reading(group.Files, ContentPart.Ends))), ReadOne)
                    .ConfigureAwait(false);
            }
            finally
            {
                // Every reader has ended by now, whether the run ended, stopped or failed.
                foreach (var checksum in _idle)
                {
                    checksum.Dispose();
                }
            }

            measure.Finished();
        }

        /// <summary>Every file of <paramref name="group"/>, in its lane, behind the reads of groups that could free more.</summary>
        private IEnumerable<(int Lane, FileRead Item, long Priority)> Reads(Reading group)
        {
            var space = Space(group.Files);

            for (var file = 0; file < group.Files.Count; file++)
            {
                yield return (lanes.LaneOf(group.Files[file].Volume), new FileRead(group, file), space);
            }
        }

        private void ReadOne(FileRead read)
        {
            var (group, file) = read;
            var candidate = group.Files[file];

            group.Checksums[file] = matching.Read(candidate, group.Part, algorithm, _idle, ct);
            measure.Done(ContentReader.BytesOf(group.Part, candidate.Length));

            if (Interlocked.Decrement(ref group.Unread) == 0)
            {
                Settle(group);
            }
        }

        /// <summary>
        /// Groups the files of one finished group by what was read, and confirms each match that read
        /// every byte or queues it to be read in full. A file alone in its group has no match.
        /// </summary>
        private void Settle(Reading group)
        {
            var length = group.Files[0].Length;
            var whole = ContentReader.IsWhole(group.Part, length);
            var goingOn = 0;

            var matches = group.Files
                .Select((file, i) => (File: file, Checksum: group.Checksums[i]))
                .Where(read => read.Checksum is not null)
                .GroupBy(read => read.Checksum!.Value)
                .Where(match => match.Count() > 1);

            foreach (var match in matches)
            {
                IReadOnlyList<DuplicateCandidate> files = [.. match.Select(read => read.File)];

                lock (matching._confirming)
                {
                    // Under the same lock as every other confirmation, so none is passed on after the
                    // search was stopped, and the caller is never given two at once.
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (whole)
                    {
                        confirmed(new DuplicateGroup(criteria, length, match.Key, files));
                        continue;
                    }
                }

                goingOn += files.Count;
                foreach (var (lane, item, priority) in Reads(new Reading(files, ContentPart.Whole)))
                {
                    _workers.Add(lane, item, priority);
                }
            }

            if (!whole)
            {
                measure.Dropped(length * (group.Files.Count - goingOn));
            }
        }
    }
}
