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

    /// <summary>
    /// Two volumes on one spinning disk share its heads, so reading both at once makes each seek
    /// past the other. However many files each holds, one is read at a time.
    /// </summary>
    [Fact]
    public async Task TwoVolumesOnOneSpinningDiskShareOneReader()
    {
        var queries = new FakeStorageQueries().Volume(@"X:\", 0).Volume(@"Y:\", 0).Disk(0, Sata, seekPenalty: true);
        var reads = new HeldReads(hold: TimeSpan.FromMilliseconds(5));

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
    /// until the bound's worth are running, or long enough that they would have been.
    /// </summary>
    [Fact]
    public async Task ASolidStateDiskIsReadByTheBoundAndNoMore()
    {
        var queries = new FakeStorageQueries().Volume(@"X:\", 1).Disk(1, Nvme, seekPenalty: false);
        var reads = new HeldReads(hold: TimeSpan.FromMilliseconds(20));

        await Matching(queries, reads.Read).MatchAsync(
            [Group(@"X:\", 1000, 3 * ReadingLanes.SolidStateReaders)], MatchCriteria.Content, ChecksumAlgorithm.XxHash128, _ => { }, null, default);

        Assert.Equal(ReadingLanes.SolidStateReaders, reads.Peak);
    }

    /// <summary>
    /// A disk Windows would not describe may spin, so its volume is read by one, and alone: without
    /// its disks it cannot be put with the other volumes on them.
    /// </summary>
    [Fact]
    public async Task AVolumeWhoseDisksAreUnknownIsReadByOne()
    {
        var reads = new HeldReads(hold: TimeSpan.FromMilliseconds(5));

        await Matching(new FakeStorageQueries(), reads.Read).MatchAsync(
            [Group(@"X:\", 1000, 6)], MatchCriteria.Content, ChecksumAlgorithm.XxHash128, _ => { }, null, default);

        Assert.Equal(1, reads.Peak);
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

    /// <summary>Reads that each take a while, counting how many ran at once at most.</summary>
    private sealed class HeldReads(TimeSpan hold)
    {
        private int _running;
        private int _peak;
        private int _count;

        public int Peak => Volatile.Read(ref _peak);

        public int Count => Volatile.Read(ref _count);

        public ContentReading Read(DuplicateCandidate file, ContentPart part, Checksum checksum, CancellationToken ct)
        {
            var running = Interlocked.Increment(ref _running);
            Interlocked.Increment(ref _count);

            for (var peak = Peak; running > peak; peak = Peak)
            {
                Interlocked.CompareExchange(ref _peak, running, peak);
            }

            Thread.Sleep(hold);
            Interlocked.Decrement(ref _running);

            return Same(checksum);
        }
    }
}
