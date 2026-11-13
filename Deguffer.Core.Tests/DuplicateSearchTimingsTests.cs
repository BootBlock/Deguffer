using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A search handed a <see cref="DuplicateSearchTimings"/> says what each part kept and asked about,
/// what each stage of the comparison read, and how long each lane took, from its first read starting
/// to its last ending, every lane and stage apart (§7.4).
/// </summary>
public sealed class DuplicateSearchTimingsTests : IDisposable
{
    private const int Block = ContentReader.BlockBytes;

    private static readonly TimeSpan EachRead = TimeSpan.FromMilliseconds(1);

    private readonly DuplicateTree _tree = new();
    private readonly ManualTimeProvider _clock = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>
    /// Three small copies, one of them with a second name; two large copies; two large files parted
    /// only by their middles; two parted by their first blocks; and one file of a length no other
    /// has. The walk keeps every name, identification asks about every name that shares a length and
    /// lists the names of the one file with two, and the comparison reads the ends of nine files and
    /// the whole of four.
    /// </summary>
    [Fact]
    public async Task EachPartSaysWhatItKeptAskedAboutAndRead()
    {
        WriteTree();
        var timings = new DuplicateSearchTimings(_clock);

        var result = await SearchAsync(timings);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(11, timings.Kept);
        Assert.Equal(10, timings.Identified);
        Assert.Equal(1, timings.Named);
        Assert.Equal(new ContentReadTally(9, 0, 0, (3 * 5000) + (6 * 2 * Block), timings.Ends.Elapsed), timings.Ends);
        Assert.Equal(new ContentReadTally(4, 0, 0, 4 * 3 * Block, timings.Whole.Elapsed), timings.Whole);
    }

    /// <summary>
    /// Each read takes a millisecond of a clock nothing else moves, and the undescribed scratch drive
    /// is one lane of one reader, so its thirteen reads run one after another: the lane and the
    /// comparison each took thirteen milliseconds, and each stage, from its first read to its last,
    /// no more than that, though the two overlap and so add up to more.
    /// </summary>
    [Fact]
    public async Task EachLaneAndEachStageIsTimedFromItsFirstReadToItsLast()
    {
        WriteTree();
        var timings = new DuplicateSearchTimings(_clock);

        await SearchAsync(timings);

        var lane = Assert.Single(timings.Lanes);
        Assert.Equal(1, lane.Readers);
        Assert.Equal(13 * EachRead, lane.Elapsed);
        Assert.Equal(13 * EachRead, timings.Of(DuplicateSearchPart.Comparison));
        Assert.InRange(timings.Ends.Elapsed, 9 * EachRead, 13 * EachRead);
        Assert.InRange(timings.Whole.Elapsed, 4 * EachRead, 13 * EachRead);
        Assert.True(timings.Ends.Elapsed + timings.Whole.Elapsed > lane.Elapsed, "The full reads were not read beside the blocks.");
        Assert.Equal(TimeSpan.Zero, timings.Of(DuplicateSearchPart.Scan));
    }

    /// <summary>
    /// A read a remembered checksum stood in for is counted as remembered and reads no byte, so a
    /// second search over unchanged files reads none.
    /// </summary>
    [Fact]
    public async Task ARememberedReadIsCountedAndReadsNoByte()
    {
        WriteTree();
        var store = ChecksumCache.FileOf(_tree.Environment);
        var first = new ChecksumCache(store, _clock);
        await SearchAsync(new DuplicateSearchTimings(_clock), first);
        await first.WhenStoredAsync(CancellationToken.None);
        var timings = new DuplicateSearchTimings(_clock);

        await SearchAsync(timings, new ChecksumCache(store, _clock));

        Assert.Equal((9, 9, 0L), (timings.Ends.Files, timings.Ends.Remembered, timings.Ends.Bytes));
        Assert.Equal((4, 4, 0L), (timings.Whole.Files, timings.Whole.Remembered, timings.Whole.Bytes));
    }

    /// <summary>A file that changed after it was identified is a read that left its file out, and none of its bytes count as read.</summary>
    [Fact]
    public async Task AReadThatLeftItsFileOutIsCountedAsLeftOut()
    {
        WriteTree();
        var timings = new DuplicateSearchTimings(_clock);

        await _tree.Searcher((file, part, checksum, ct) => file.Name == "d1.bin"
                ? ContentReading.LeftOut(ContentReadResult.Changed)
                : ContentReader.Default.Read(file, part, checksum, ct))
            .SearchAsync(Search(), _tree.Policy(), timings: timings);

        Assert.Equal((9, 1), (timings.Ends.Files, timings.Ends.LeftOut));
        Assert.Equal((3 * 5000) + (5 * 2 * Block), timings.Ends.Bytes);
    }

    private async Task<DuplicateSearchResult> SearchAsync(DuplicateSearchTimings timings, ChecksumCache? cache = null)
    {
        var reader = cache is null
            ? ContentReader.Default
            : new ContentReader(FileInformation.Default, FileInformation.Default.Hold, FileInformation.OpenById, _ => throw new IOException(), cache);

        return await _tree.Searcher(
                (file, part, checksum, ct) =>
                {
                    var reading = reader.Read(file, part, checksum, ct);
                    _clock.Advance(EachRead);
                    return reading;
                },
                cache)
            .SearchAsync(Search(), _tree.Policy(), timings: timings);
    }

    private DuplicateSearch Search() => new(MatchCriteria.Content, [new SearchLocation(Path.Combine(_tree.Top, "Data"))]);

    private void WriteTree()
    {
        var small = Random(5000, 1);
        var named = _tree.File(small, "Data", "a1.bin");
        HardLink.To(named, Path.Combine(_tree.Top, "Data", "y", "a1-again.bin"));
        _tree.File(small, "Data", "x", "a2.bin");
        _tree.File(small, "Data", "y", "a3.bin");

        var large = Random(3 * Block, 2);
        _tree.File(large, "Data", "b1.bin");
        _tree.File(large, "Data", "x", "b2.bin");

        var middle = Random(3 * Block, 3);
        _tree.File(middle, "Data", "c1.bin");
        middle[Block + 1] ^= 0xFF;
        _tree.File(middle, "Data", "x", "c2.bin");

        var start = Random(3 * Block, 4);
        _tree.File(start, "Data", "d1.bin");
        start[0] ^= 0xFF;
        _tree.File(start, "Data", "x", "d2.bin");

        _tree.File(Random(7000, 5), "Data", "lone.bin");
    }

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);

        return bytes;
    }
}
