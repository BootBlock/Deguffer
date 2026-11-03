using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Files are read in lanes, one a physical disk, bounded by what the disk is (§7.4). Nothing here
/// reads a file: the reads are counted and held, on drives that exist only in the fakes.
/// </summary>
public sealed class ContentMatchingTests
{
    private const byte Sata = 0x0B;
    private const byte Nvme = 0x11;

    /// <summary>Longer than a file's first and last blocks, so its blocks matching leaves it to be read in full.</summary>
    private const long Large = 3L * ContentReader.BlockBytes;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Two volumes on one spinning disk share its heads, so reading both at once makes each seek
    /// past the other. However many files each holds, one is read at a time.
    /// </summary>
    [Fact]
    public async Task TwoVolumesOnOneSpinningDiskShareOneReader()
    {
        var queries = new FakeStorageQueries().Volume(@"X:\", 0).Volume(@"Y:\", 0).Disk(0, Sata, seekPenalty: true);
        using var reads = new HeldReads(hold: TimeSpan.FromMilliseconds(5));

        await Matching(queries, reads.Read).MatchAsync(
            [Group(@"X:\", 1000, 8), Group(@"Y:\", 2000, 8)], MatchCriteria.Content, ChecksumAlgorithm.XxHash128, _ => { }, null, default);

        Assert.Equal(16, reads.Count);
        Assert.Equal(1, reads.Peak);
    }

    /// <summary>
    /// Two solid-state disks are read at the same time. Each read here waits until a read is running
    /// on the other disk too, which only happens where the two are read at once.
    /// </summary>
    [Fact]
    public async Task TwoSolidStateDisksAreReadAtOnce()
    {
        var queries = new FakeStorageQueries()
            .Volume(@"X:\", 1).Disk(1, Nvme, seekPenalty: false)
            .Volume(@"Y:\", 2).Disk(2, Sata, seekPenalty: false);
        using var both = new Barrier(2);
        var met = 0;

        ContentReading Read(DuplicateCandidate file, ContentPart part, Checksum checksum, CancellationToken ct)
        {
            if (both.SignalAndWait(TimeSpan.FromSeconds(10), ct))
            {
                Interlocked.Increment(ref met);
            }

            return Same(checksum);
        }

        await Matching(queries, Read).MatchAsync(
            [new CandidateGroup(100, null, null, [File(@"X:\a.bin", 100), File(@"Y:\a.bin", 100)])],
            MatchCriteria.Content, ChecksumAlgorithm.XxHash128, _ => { }, null, default);

        Assert.Equal(2, met);
    }

    /// <summary>
    /// A solid-state disk is read by several readers, up to the bound and no further. Each read is held
    /// until the bound's worth are running, and then for a while longer, in which a reader past the
    /// bound would have started too. A lane that never starts the bound's worth fails on its own
    /// message, so a busy machine is never what decides how many readers are seen.
    /// </summary>
    [Fact]
    public async Task ASolidStateDiskIsReadByTheBoundAndNoMore()
    {
        var queries = new FakeStorageQueries().Volume(@"X:\", 1).Disk(1, Nvme, seekPenalty: false);
        using var reads = new HeldReads(hold: TimeSpan.FromMilliseconds(20), gather: ReadingLanes.SolidStateReaders);

        await Matching(queries, reads.Read).MatchAsync(
            [Group(@"X:\", 1000, 3 * ReadingLanes.SolidStateReaders)], MatchCriteria.Content, ChecksumAlgorithm.XxHash128, _ => { }, null, default);

        Assert.False(
            reads.GatherTimedOut,
            $"The lane had {reads.PeakAtGatherLimit} of its {ReadingLanes.SolidStateReaders} readers running at once after {HeldReads.GatherAtMost.TotalSeconds} s.");
        Assert.Equal(ReadingLanes.SolidStateReaders, reads.Peak);
    }

    /// <summary>
    /// A read blocks its thread for as long as the disk takes, and the pool has as many threads as the
    /// machine has processors until it finds itself starved, so a lane read there is read by fewer
    /// than its bound, and holds the threads the rest of the process's pool work waits for.
    /// </summary>
    [Fact]
    public async Task NoFileIsReadOnThePool()
    {
        var queries = new FakeStorageQueries()
            .Volume(@"X:\", 1).Disk(1, Nvme, seekPenalty: false)
            .Volume(@"Y:\", 2).Disk(2, Sata, seekPenalty: true);
        using var reads = new HeldReads(hold: TimeSpan.FromMilliseconds(1));

        await Matching(queries, reads.Read).MatchAsync(
            [Group(@"X:\", 1000, 3 * ReadingLanes.SolidStateReaders), Group(@"Y:\", 2000, 4)],
            MatchCriteria.Content, ChecksumAlgorithm.XxHash128, _ => { }, null, default);

        Assert.Equal((3 * ReadingLanes.SolidStateReaders) + 4, reads.Count);
        Assert.Equal(0, reads.OnThePool);
    }

    /// <summary>
    /// A disk Windows would not describe may spin, so its volume is read by one, and alone: without
    /// its disks it cannot be put with the other volumes on them.
    /// </summary>
    [Fact]
    public async Task AVolumeWhoseDisksAreUnknownIsReadByOne()
    {
        using var reads = new HeldReads(hold: TimeSpan.FromMilliseconds(5));

        await Matching(new FakeStorageQueries(), reads.Read).MatchAsync(
            [Group(@"X:\", 1000, 6)], MatchCriteria.Content, ChecksumAlgorithm.XxHash128, _ => { }, null, default);

        Assert.Equal(1, reads.Peak);
    }

    /// <summary>
    /// A fast disk reads its full content while a slow one is still reading blocks. The spinning
    /// disk's reads of its ends are held until a file on the solid-state disk is read in full, which
    /// can only happen where the full reads start before every disk has read every file's ends.
    /// </summary>
    [Fact]
    public async Task AFastDiskReadsInFullWhileASlowOneIsStillReadingBlocks()
    {
        var queries = new FakeStorageQueries()
            .Volume(@"X:\", 1).Disk(1, Nvme, seekPenalty: false)
            .Volume(@"Y:\", 2).Disk(2, Sata, seekPenalty: true);
        using var readInFull = new ManualResetEventSlim(initialState: false);
        var heldUntilAFullRead = true;

        ContentReading Read(DuplicateCandidate file, ContentPart part, Checksum checksum, CancellationToken ct)
        {
            if (file.Path.StartsWith(@"Y:\", StringComparison.Ordinal))
            {
                if (!readInFull.Wait(Patience, ct))
                {
                    Volatile.Write(ref heldUntilAFullRead, false);
                    readInFull.Set();
                }
            }
            else if (part is ContentPart.Whole)
            {
                readInFull.Set();
            }

            return Same(checksum);
        }

        await Matching(queries, Read).MatchAsync(
            [Group(@"X:\", Large, 2), Group(@"Y:\", 1000, 2)], MatchCriteria.Content, ChecksumAlgorithm.XxHash128, _ => { }, null, default);

        Assert.True(Volatile.Read(ref heldUntilAFullRead), $"No file on the fast disk was read in full within {Patience.TotalSeconds} s of the slow disk's first read.");
    }

    /// <summary>
    /// Within a disk, a group's full reads overtake the blocks of the smaller groups still waiting,
    /// so the group that could free the most is confirmed first. One reader, so the order is exact.
    /// </summary>
    [Fact]
    public async Task AGroupsFullReadsOvertakeTheBlocksOfSmallerGroups()
    {
        List<(string Name, ContentPart Part)> order = [];

        ContentReading Read(DuplicateCandidate file, ContentPart part, Checksum checksum, CancellationToken ct)
        {
            lock (order)
            {
                order.Add((file.Name, part));
            }

            return Same(checksum);
        }

        await Matching(new FakeStorageQueries(), Read).MatchAsync(
            [Group(@"X:\", 1000, 2), Group(@"X:\", 2000, 2), Group(@"X:\", Large, 2)],
            MatchCriteria.Content, ChecksumAlgorithm.XxHash128, _ => { }, null, default);

        Assert.Equal(
            [($"{Large}-0.bin", ContentPart.Ends), ($"{Large}-1.bin", ContentPart.Ends), ($"{Large}-0.bin", ContentPart.Whole), ($"{Large}-1.bin", ContentPart.Whole)],
            order.Take(4));
        Assert.Equal(8, order.Count);
    }

    /// <summary>
    /// A spinning disk is read by one at a time in both stages, however its full reads are queued:
    /// each group here is settled by whichever disk reads its last file, and two of its three files
    /// are on the solid-state disk, so most of the spinning disk's full reads are queued from there.
    /// </summary>
    [Fact]
    public async Task ASpinningDiskIsReadByOneInBothStages()
    {
        var queries = new FakeStorageQueries()
            .Volume(@"X:\", 0).Disk(0, Sata, seekPenalty: true)
            .Volume(@"Y:\", 1).Disk(1, Nvme, seekPenalty: false);
        using var spinning = new HeldReads(hold: TimeSpan.FromMilliseconds(5));
        var parts = new int[2];

        ContentReading Read(DuplicateCandidate file, ContentPart part, Checksum checksum, CancellationToken ct)
        {
            if (!file.Path.StartsWith(@"X:\", StringComparison.Ordinal))
            {
                return Same(checksum);
            }

            Interlocked.Increment(ref parts[(int)part]);
            return spinning.Read(file, part, checksum, ct);
        }

        var confirmed = 0;
        await Matching(queries, Read).MatchAsync(
            [.. Enumerable.Range(1, 6).Select(i => new CandidateGroup(
                Large * i, null, null, [File($@"X:\{i}.bin", Large * i), File($@"Y:\{i}-a.bin", Large * i), File($@"Y:\{i}-b.bin", Large * i)]))],
            MatchCriteria.Content, ChecksumAlgorithm.XxHash128, _ => Interlocked.Increment(ref confirmed), null, default);

        Assert.Equal([6, 6], parts);
        Assert.Equal(1, spinning.Peak);
        Assert.Equal(6, confirmed);
    }

    /// <summary>
    /// The share read only grows, read after read, across both stages at once: the bytes read never
    /// go back and the total never rises, and the search ends with the two equal. The clock moves a
    /// second at every read, and both volumes are on one spinning disk, read one file at a time, so
    /// every read is reported. One group matches to the end, one stops matching at its ends, and one
    /// is small enough to be read whole at once.
    /// </summary>
    [Fact]
    public async Task TheShareReadOnlyGrowsReadAfterRead()
    {
        var queries = new FakeStorageQueries().Volume(@"X:\", 0).Volume(@"Y:\", 0).Disk(0, Sata, seekPenalty: true);
        var clock = new ManualTimeProvider();
        List<DuplicateSearchProgress> reports = [];

        ContentReading Read(DuplicateCandidate file, ContentPart part, Checksum checksum, CancellationToken ct)
        {
            clock.Advance(TimeSpan.FromSeconds(1));

            // Every file of the second large group differs from the others at its ends.
            checksum.Append(System.Text.Encoding.UTF8.GetBytes(file.Path.Contains("differs", StringComparison.Ordinal) ? file.Path : "same"));
            return new ContentReading(ContentReadResult.Read, checksum.Finish());
        }

        CandidateGroup Spread(string name, long length) => new(
            length, null, null, [File($@"X:\{name}-1.bin", length), File($@"Y:\{name}-2.bin", length), File($@"X:\{name}-3.bin", length)]);

        await new ContentMatching(Read, new VolumeMediaCache(queries), clock).MatchAsync(
            [Spread("matches", Large), Spread("differs", Large + 1), Spread("small", 1000)],
            MatchCriteria.Content,
            ChecksumAlgorithm.XxHash128,
            _ => { },
            new CallbackProgress<DuplicateSearchProgress>(report =>
            {
                lock (reports)
                {
                    reports.Add(report);
                }
            }),
            default);

        const long Ends = 2 * ContentReader.BlockBytes;
        var read = (3 * (Ends + Large)) + (3 * Ends) + (3 * 1000);
        Assert.Equal(read + (3 * (Large + 1)), reports[0].Total);
        Assert.Equal((read, read), (reports[^1].Done, reports[^1].Total!.Value));
        // The first, one at each of the twelve reads, and the last.
        Assert.True(reports.Count >= 14, $"Only {reports.Count} reports were made, so not every read was seen.");

        foreach (var (before, after) in reports.Zip(reports.Skip(1)))
        {
            Assert.True(after.Done >= before.Done, $"{after.Done} bytes read came after {before.Done}.");
            Assert.True(after.Total <= before.Total, $"A total of {after.Total} came after {before.Total}.");
        }
    }

    private static ContentMatching Matching(FakeStorageQueries queries, ReadContent read) =>
        new(read, new VolumeMediaCache(queries));

    private static CandidateGroup Group(string root, long length, int files) =>
        new(length, null, null, [.. Enumerable.Range(0, files).Select(i => File(Path.Combine(root, $"{length}-{i}.bin"), length))]);

    private static DuplicateCandidate File(string path, long length) => new(
        new FileIdentity(1, (UInt128)(uint)path.GetHashCode(StringComparison.Ordinal)),
        path,
        Path.GetFileName(path),
        [path],
        NameCount: 1,
        length,
        SizeOnDisk: length,
        DateTime.UnixEpoch,
        FileStorage.Plain,
        LocationRole.Search)
    {
        Volume = new LocalVolume(Path.GetPathRoot(path)!, DriveType.Fixed, VolumeReadiness.Ready),
    };

    /// <summary>Every file the same, so every group matches and nothing about the reads depends on content.</summary>
    private static ContentReading Same(Checksum checksum)
    {
        checksum.Append("same"u8);
        return new ContentReading(ContentReadResult.Read, checksum.Finish());
    }

    /// <summary>
    /// Reads that each take a while, counting how many ran at once at most. Where
    /// <paramref name="gather"/> is more than one, every read first waits until that many are
    /// running, or until <see cref="GatherAtMost"/> passes, which <see cref="GatherTimedOut"/> records
    /// with the most seen at once by then, so how soon each reader's thread starts never decides how
    /// many are seen at once.
    /// </summary>
    private sealed class HeldReads(TimeSpan hold, int gather = 1) : IDisposable
    {
        public static readonly TimeSpan GatherAtMost = TimeSpan.FromSeconds(10);

        private readonly ManualResetEventSlim _gathered = new(initialState: false);
        private int _running;
        private int _peak;
        private int _count;
        private int _onThePool;
        private bool _gatherTimedOut;
        private int _peakAtGatherLimit;

        public int Peak => Volatile.Read(ref _peak);

        public int Count => Volatile.Read(ref _count);

        public int OnThePool => Volatile.Read(ref _onThePool);

        public bool GatherTimedOut => Volatile.Read(ref _gatherTimedOut);

        /// <summary>
        /// The most reads running at once when the gather ran out of time. The reads it then lets go
        /// can raise <see cref="Peak"/> after the limit, which a timed-out gather must not report.
        /// </summary>
        public int PeakAtGatherLimit => Volatile.Read(ref _peakAtGatherLimit);

        public void Dispose() => _gathered.Dispose();

        public ContentReading Read(DuplicateCandidate file, ContentPart part, Checksum checksum, CancellationToken ct)
        {
            var running = Interlocked.Increment(ref _running);
            Interlocked.Increment(ref _count);

            if (Thread.CurrentThread.IsThreadPoolThread)
            {
                Interlocked.Increment(ref _onThePool);
            }

            for (var peak = Peak; running > peak; peak = Peak)
            {
                Interlocked.CompareExchange(ref _peak, running, peak);
            }

            // Once, for the first reads: the rest only need holding, and a lane read by fewer than
            // were asked for waits out the limit once rather than at every read.
            if (running >= gather)
            {
                _gathered.Set();
            }
            else if (!_gathered.Wait(GatherAtMost, ct))
            {
                Volatile.Write(ref _peakAtGatherLimit, Peak);
                Volatile.Write(ref _gatherTimedOut, true);
                _gathered.Set();
            }

            Thread.Sleep(hold);
            Interlocked.Decrement(ref _running);

            return Same(checksum);
        }
    }
}
