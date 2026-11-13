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
    /// <param name="timings">Given each read by its lane and part, for a caller that measures the search, or null.</param>
    /// <exception cref="OperationCanceledException">The search was stopped; the groups already given stand.</exception>
    public async Task MatchAsync(
        IReadOnlyList<CandidateGroup> groups,
        MatchCriteria criteria,
        ChecksumAlgorithm algorithm,
        Action<DuplicateGroup> confirmed,
        IProgress<DuplicateSearchProgress>? progress,
        CancellationToken ct,
        DuplicateSearchTimings? timings = null)
    {
        var lanes = ReadingLanes.Of(groups.SelectMany(group => group.Files).Select(file => file.Volume), _media);

        var run = new Run(
            this,
            lanes,
            new StageMeasure(DuplicateSearchStage.ComparingContent, groups.SelectMany(group => group.Files).Sum(file => ContentReader.MostToRead(file.Length)), progress, _clock),
            timings?.Comparing(lanes.Readers),
            criteria,
            algorithm,
            confirmed,
            ct);

        await run.MatchAsync(groups).ConfigureAwait(false);
    }

    /// <summary>
    /// What became of reading one file's <paramref name="part"/>, counted where it was left out,
    /// through a running checksum by <paramref name="algorithm"/> no other reader holds at the time.
    /// </summary>
    private ContentReading Read(DuplicateCandidate file, ContentPart part, ChecksumAlgorithm algorithm, IdleChecksums idle, CancellationToken ct)
    {
        var checksum = idle.Take(algorithm);

        try
        {
            var reading = _read(file, part, checksum, ct);

            switch (reading.Result)
            {
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

            return reading;
        }
        finally
        {
            idle.Return(checksum);
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

    /// <summary>
    /// The running checksums no reader holds at the time, apart by algorithm, since a run reads the
    /// ends of its larger files by one and everything else by the one chosen
    /// (<see cref="ContentReader.AlgorithmOf"/>). A reader takes one rather than making one for each
    /// file (G5).
    /// </summary>
    private sealed class IdleChecksums : IDisposable
    {
        private readonly ConcurrentDictionary<ChecksumAlgorithm, ConcurrentBag<Checksum>> _idle = new();

        public Checksum Take(ChecksumAlgorithm algorithm) =>
            Of(algorithm).TryTake(out var free) ? free : Checksum.For(algorithm);

        public void Return(Checksum checksum) => Of(checksum.Algorithm).Add(checksum);

        public void Dispose()
        {
            foreach (var checksum in _idle.Values.SelectMany(idle => idle))
            {
                checksum.Dispose();
            }
        }

        private ConcurrentBag<Checksum> Of(ChecksumAlgorithm algorithm) => _idle.GetOrAdd(algorithm, static _ => []);
    }

    /// <summary>One file of a <see cref="Reading"/>, by its place there, and the lane that reads it.</summary>
    private readonly record struct FileRead(Reading Group, int File, int Lane);

    /// <summary>One call of <see cref="MatchAsync"/>: its lanes, its measure, and the running checksums its readers share.</summary>
    /// <param name="timed">What records each read for a caller that measures the search, or null.</param>
    private sealed class Run(
        ContentMatching matching,
        ReadingLanes lanes,
        StageMeasure measure,
        ContentLaneSpans? timed,
        MatchCriteria criteria,
        ChecksumAlgorithm algorithm,
        Action<DuplicateGroup> confirmed,
        CancellationToken ct)
    {
        private readonly LaneWorkers<FileRead> _workers = new(lanes.Readers, ct);
        private readonly IdleChecksums _idle = new();

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
                _idle.Dispose();
            }

            measure.Finished();
        }

        /// <summary>Every file of <paramref name="group"/>, in its lane, behind the reads of groups that could free more.</summary>
        private IEnumerable<(int Lane, FileRead Item, long Priority)> Reads(Reading group)
        {
            var space = Space(group.Files);

            for (var file = 0; file < group.Files.Count; file++)
            {
                var lane = lanes.LaneOf(group.Files[file].Volume);

                yield return (lane, new FileRead(group, file, lane), space);
            }
        }

        private void ReadOne(FileRead read)
        {
            var (group, file, lane) = read;
            var candidate = group.Files[file];
            var bytes = ContentReader.BytesOf(group.Part, candidate.Length);
            var started = timed?.Now() ?? 0;

            var reading = matching.Read(
                candidate, group.Part, ContentReader.AlgorithmOf(group.Part, candidate.Length, algorithm), _idle, ct);

            timed?.Read(lane, group.Part, reading, bytes, started);
            group.Checksums[file] = reading.Result is ContentReadResult.Read ? reading.Checksum : null;
            measure.Done(bytes);

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
