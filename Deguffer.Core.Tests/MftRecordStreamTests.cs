using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the record stream says about the table as a whole, apart from what either caller makes of
/// the records it hands on.
/// </summary>
public sealed class MftRecordStreamTests
{
    /// <summary>
    /// Each extension record belongs to one base record, so a table whose lists want more of them
    /// than it holds was not written by NTFS. Following every want of a hostile one would hold
    /// memory without bound, so the stream stops and says the table was not read in full, which
    /// both callers already know how to fall back from. Inside a record the wants are counted in
    /// the first pass, and outside one in the second.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaysATableWantingMoreExtensionRecordsThanItHoldsWasNotReadInFull(bool outsideTheRecord)
    {
        using var source = new MftFixture()
            .AddFileWhoseListWantsMoreRecordsThanTheTableHolds(20, outsideTheRecord)
            .Build();

        Assert.True(source.RecordCount < 24, "the fixture's list must name more records than the table holds");
        Assert.False(MftRecordStream.TryReadAll(source, (int)source.RecordCount, TableTuning.Default, static (_, _, in _) => true, onProgress: null, default));
    }

    /// <summary>
    /// Where the first pass meets the record that takes the wants past the table's size, it stops
    /// there rather than reading on and holding the wants of every record after it. Read without a
    /// bitmap, so the free record just before it is handed on too.
    /// </summary>
    [Fact]
    public void StopsReadingAtTheRecordWhoseWantsOutgrowTheTable()
    {
        using var source = new MftFixture()
            .AddFileWhoseListWantsMoreRecordsThanTheTableHolds(20, outsideTheRecord: false)
            .AddFile(21, MftRecord.RootRecordNumber, "after.tgz", allocated: 4096, logical: 4000)
            .WithoutBitmap()
            .Build();
        var handed = new List<long>();

        MftRecordStream.TryReadAll(source, (int)source.RecordCount, TableTuning.Default, (number, _, in _) =>
        {
            handed.Add(number);
            return true;
        }, onProgress: null, default);

        Assert.Contains(19L, handed);
        Assert.DoesNotContain(21L, handed);
    }

    /// <summary>
    /// The first pass keeps what every extension record holds as it reads it, so the second pass
    /// reads no record again: not one after its owner, not one before it, not one holding a name,
    /// and not one named by a list kept outside the table. Each file still gets what its extension
    /// record holds.
    /// </summary>
    [Fact]
    public void ReadsNoRecordAgainToFollowAnAttributeList()
    {
        using var table = new MftFixture()
            .AddFileWithDataInAnExtensionRecord(20, MftRecord.RootRecordNumber, "after.tgz", allocated: 8192, logical: 8000, extension: 1_000)
            .AddFileWithDataInAnExtensionRecord(30, MftRecord.RootRecordNumber, "before.tgz", allocated: 4096, logical: 3000, extension: 25)
            .AddFileWithItsNameInAnExtensionRecord(40, MftRecord.RootRecordNumber, "named.dll", allocated: 8192, logical: 5000, extension: 41)
            .AddFileWithANonResidentAttributeList(
                50, MftRecord.RootRecordNumber, "outside.tgz", allocated: 12_288, logical: 12_000, extension: 51, listCluster: 500)
            .Build();
        using var source = new RecordedReads(table);

        var handed = ReadAll(source, new TableTuning(TableTuning.MinimumReadBytes, readsInFlight: 4, parseThreads: 4), out var wholeTable);

        Assert.True(wholeTable);
        Assert.Empty(source.Reads);
        Assert.Equal(8000, handed[20].Record.Size?.Logical);
        Assert.Equal(3000, handed[30].Record.Size?.Logical);
        Assert.Equal("named.dll", handed[40].Record.Name);
        Assert.Equal(12_000, handed[50].Record.Size?.Logical);
    }

    /// <summary>
    /// A wanted record past where the first pass stopped was never read, so it is read now, and it
    /// is the only kind that is. Records close together are read in one read, and a long run of
    /// records nobody wants is never read across to reach the next, however large a read may be.
    /// </summary>
    [Fact]
    public void ReadsTheExtensionRecordsPastWhereTheFirstPassStopped()
    {
        using var table = new MftFixture()
            .AddFileWithDataInAnExtensionRecord(20, MftRecord.RootRecordNumber, "a.tgz", allocated: 8192, logical: 8000, extension: 1_000)
            .AddFileWithDataInAnExtensionRecord(21, MftRecord.RootRecordNumber, "b.tgz", allocated: 8192, logical: 7000, extension: 1_002)
            .AddFileWithDataInAnExtensionRecord(22, MftRecord.RootRecordNumber, "c.tgz", allocated: 8192, logical: 6000, extension: 3_000)
            .WithoutBitmap()
            .Build();
        using var source = new RecordedReads(table, unreadableFrom: 500, unreadableTo: 600);

        var handed = ReadAll(source, new TableTuning(TableTuning.MaximumReadBytes, 4, 2), out var wholeTable);

        Assert.False(wholeTable);
        Assert.Equal([(1_000L, 3), (3_000L, 1)], source.Reads);
        Assert.Equal(8000, handed[20].Record.Size?.Logical);
        Assert.Equal(7000, handed[21].Record.Size?.Logical);
        Assert.Equal(6000, handed[22].Record.Size?.Logical);
    }

    /// <summary>
    /// Two files whose lists name one extension record, which only a damaged table holds. It is the
    /// first file's, so the first gets its size from it, and the second loses its size and keeps its
    /// place. What the first pass kept of it serves both, and neither changes it for the other.
    /// </summary>
    [Fact]
    public void GivesAnExtensionRecordTwoFilesNameOnlyToTheFileItBelongsTo()
    {
        // Added second, so the extension record the table holds is the one owned by 20.
        using var source = new MftFixture()
            .AddFileWithDataInAnExtensionRecord(21, MftRecord.RootRecordNumber, "claims.tgz", allocated: 4096, logical: 4000, extension: 22)
            .AddFileWithDataInAnExtensionRecord(20, MftRecord.RootRecordNumber, "owns.tgz", allocated: 8192, logical: 8000, extension: 22)
            .Build();

        var handed = ReadAll(source, TableTuning.Default, out _);

        Assert.Equal(MftParseOutcome.Parsed, handed[20].Outcome);
        Assert.Equal(8000, handed[20].Record.Size?.Logical);
        Assert.Equal(MftParseOutcome.Parsed, handed[21].Outcome);
        Assert.Equal("claims.tgz", handed[21].Record.Name);
        Assert.Null(handed[21].Record.Size);
    }

    /// <summary>
    /// Lists kept outside the table are read several at once, and in the order of their clusters
    /// rather than of the records naming them, so the disk moves forward through the volume. Each
    /// read here waits until all three are in flight, which only reads made at once can be.
    /// </summary>
    [Fact]
    public void ReadsListsKeptOutsideTheTableAtOnceInClusterOrder()
    {
        using var table = new MftFixture()
            .AddFileWithANonResidentAttributeList(20, MftRecord.RootRecordNumber, "a.tgz", allocated: 4096, logical: 1000, extension: 30, listCluster: 900)
            .AddFileWithANonResidentAttributeList(21, MftRecord.RootRecordNumber, "b.tgz", allocated: 4096, logical: 2000, extension: 31, listCluster: 700)
            .AddFileWithANonResidentAttributeList(22, MftRecord.RootRecordNumber, "c.tgz", allocated: 4096, logical: 3000, extension: 32, listCluster: 800)
            .Build();
        using var source = new HeldClusterReads(table, waitFor: 3);

        var handed = ReadAll(source, new TableTuning(TableTuning.MaximumReadBytes, readsInFlight: 4, parseThreads: 1), out var wholeTable);

        Assert.True(wholeTable);
        Assert.Equal([700L, 800L, 900L], source.Clusters);
        Assert.Equal(3, source.MostInFlight);
        Assert.Equal(1000, handed[20].Record.Size?.Logical);
        Assert.Equal(2000, handed[21].Record.Size?.Logical);
        Assert.Equal(3000, handed[22].Record.Size?.Logical);
    }

    /// <summary>
    /// A record past where the first pass stopped is read once however many lists name it: reading
    /// it runs its fixup, which rewrites it in place and cannot run twice. Here the file naming it
    /// first does not own it, so the owner, second, still has to get its size from the same read.
    /// </summary>
    [Fact]
    public void ReadsAnExtensionRecordPastTheStopOnceForEveryFileNamingIt()
    {
        // Added second, so the extension record the table holds is the one owned by 21.
        using var table = new MftFixture()
            .AddFileWithDataInAnExtensionRecord(20, MftRecord.RootRecordNumber, "claims.tgz", allocated: 4096, logical: 4000, extension: 40)
            .AddFileWithDataInAnExtensionRecord(21, MftRecord.RootRecordNumber, "owns.tgz", allocated: 8192, logical: 8000, extension: 40)
            .WithoutBitmap()
            .Build();
        using var source = new RecordedReads(table, unreadableFrom: 30, unreadableTo: 35);

        var handed = ReadAll(source, TableTuning.Default, out _);

        Assert.Equal([(40L, 1)], source.Reads);
        Assert.Equal(8000, handed[21].Record.Size?.Logical);
        Assert.Null(handed[20].Record.Size);
    }

    /// <summary>
    /// Lists kept outside the table are handed on in the order their reads were made, whatever order
    /// they complete in, so a table whose lists want more than it holds loses the same files on every
    /// run. Here the reads complete in reverse, and the budget runs out at the second list in cluster
    /// order: the first two are followed, so the records they name are read, and the third is not.
    /// </summary>
    [Fact]
    public void LosesTheSameListsWhicheverOrderTheirReadsComplete()
    {
        using var table = new MftFixture()
            .AddFileWhoseListOutsideTheTableNamesRecords(16, listCluster: 900, firstNamed: 1_000, count: 12)
            .AddFileWhoseListOutsideTheTableNamesRecords(17, listCluster: 700, firstNamed: 2_000, count: 12)
            .AddFileWhoseListOutsideTheTableNamesRecords(18, listCluster: 800, firstNamed: 3_000, count: 12)
            .Build();
        using var source = new RecordedReads(new ScriptedClusterReads(
            table, (cluster, ct) => Task.Delay(TimeSpan.FromMilliseconds(1_000 - cluster), ct)));

        Assert.True(source.RecordCount < 24, "two lists must want more than the table holds, and one must not");

        ReadAll(source, new TableTuning(TableTuning.MaximumReadBytes, readsInFlight: 4, parseThreads: 1), out var wholeTable);

        Assert.False(wholeTable);
        Assert.Contains(source.Reads, r => r.First is >= 2_000 and < 2_012);
        Assert.Contains(source.Reads, r => r.First is >= 3_000 and < 3_012);
        Assert.DoesNotContain(source.Reads, r => r.First is >= 1_000 and < 1_012);
    }

    /// <summary>
    /// A list read that fails leaves no other list read in flight: the reads still landing in their
    /// buffers are cancelled and waited for before the failure is thrown, so no buffer is released
    /// while a read can still land in it.
    /// </summary>
    [Fact]
    public void WaitsForEveryListReadInFlightBeforeAFailureLeaves()
    {
        var cancelled = false;
        using var table = new MftFixture()
            .AddFileWithANonResidentAttributeList(20, MftRecord.RootRecordNumber, "a.tgz", allocated: 4096, logical: 1000, extension: 30, listCluster: 700)
            .AddFileWithANonResidentAttributeList(21, MftRecord.RootRecordNumber, "b.tgz", allocated: 4096, logical: 2000, extension: 31, listCluster: 800)
            .Build();
        using var source = new ScriptedClusterReads(table, async (cluster, ct) =>
        {
            if (cluster == 700)
            {
                throw new IOException("The volume refused the read.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                // Lands a little after the cancellation, as a real read does, so a reader that
                // cancels without waiting returns while this is still in flight.
                cancelled = true;
                await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);
                throw;
            }
        });

        Assert.Throws<IOException>(() => ReadAll(source, new TableTuning(TableTuning.MaximumReadBytes, readsInFlight: 4, parseThreads: 1), out _));

        Assert.True(cancelled, "the read still in flight was cancelled");
        Assert.Equal(0, source.InFlight);
    }

    /// <summary>
    /// Where the lists inside records already want more than the table holds, no list kept outside
    /// the table is read: its file is lost whole, as every file past that point is, rather than
    /// followed on the word of a table NTFS did not write.
    /// </summary>
    [Fact]
    public void ReadsNoListOutsideATableAlreadyWantingMoreThanItHolds()
    {
        using var source = new MftFixture()
            .AddFileWithANonResidentAttributeList(16, MftRecord.RootRecordNumber, "outside.tgz", allocated: 4096, logical: 4000, extension: 17, listCluster: 500)
            .AddFileWhoseListWantsMoreRecordsThanTheTableHolds(20, outsideTheRecord: false)
            .Build();

        var handed = ReadAll(source, TableTuning.Default, out var wholeTable);

        Assert.False(wholeTable);
        Assert.Equal(MftParseOutcome.Unreadable, handed[16].Outcome);
    }

    /// <summary>The same stream reads a table whose lists want only what it holds to its end.</summary>
    [Fact]
    public void ReadsATableWhoseListsWantOnlyWhatItHolds()
    {
        using var source = new MftFixture()
            .AddFileWithDataInAnExtensionRecord(20, MftRecord.RootRecordNumber, "fragmented.tgz", allocated: 8192, logical: 8000, extension: 21)
            .Build();

        Assert.True(MftRecordStream.TryReadAll(source, (int)source.RecordCount, TableTuning.Default, static (_, _, in _) => true, onProgress: null, default));
    }

    /// <summary>Every record the stream handed on, by number, and whether it read the table whole.</summary>
    private static Dictionary<long, (MftParseOutcome Outcome, MftRecord Record)> ReadAll(
        IMftSource source, TableTuning tuning, out bool wholeTable)
    {
        var handed = new Dictionary<long, (MftParseOutcome Outcome, MftRecord Record)>();

        wholeTable = MftRecordStream.TryReadAll(source, (int)source.RecordCount, tuning, (number, outcome, in record) =>
        {
            lock (handed)
            {
                handed[number] = (outcome, record);
            }

            return true;
        }, onProgress: null, default);

        return handed;
    }

    /// <summary>
    /// A table that records the reads made one at a time, which only the second pass makes, and only
    /// of records the first pass never reached: the first pass reads overlapped. The records from
    /// <c>unreadableFrom</c> up to <c>unreadableTo</c> cannot be read, as a bad region in the middle of
    /// a disk cannot, and a read reaching into them stops short.
    /// </summary>
    private sealed class RecordedReads(
        IMftSource table, long unreadableFrom = long.MaxValue, long unreadableTo = long.MaxValue) : IMftSource
    {
        public List<(long First, int Records)> Reads { get; } = [];

        public int BytesPerRecord => table.BytesPerRecord;

        public long RecordCount => table.RecordCount;

        public MftBitmapPlacement? Bitmap => table.Bitmap;

        public int BatchLength(long firstRecord, int capacity) => table.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination)
        {
            Reads.Add((firstRecord, destination.Length / BytesPerRecord));
            return table.ReadBatch(firstRecord, destination[..Readable(firstRecord, destination.Length)]);
        }

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            table.ReadBatchAsync(firstRecord, destination[..Readable(firstRecord, destination.Length)], ct);

        public int BytesPerCluster => table.BytesPerCluster;

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => table.TryReadClusters(firstCluster, destination);

        public ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct) =>
            table.TryReadClustersAsync(firstCluster, destination, ct);

        public void Dispose() => table.Dispose();

        /// <summary>How many of <paramref name="bytes"/> from <paramref name="firstRecord"/> come before the bad region.</summary>
        private int Readable(long firstRecord, int bytes)
        {
            if (firstRecord >= unreadableFrom && firstRecord < unreadableTo)
            {
                return 0;
            }

            return firstRecord < unreadableFrom
                ? (int)Math.Min(bytes / BytesPerRecord, unreadableFrom - firstRecord) * BytesPerRecord
                : bytes;
        }
    }

    /// <summary>
    /// A table whose overlapped cluster reads each run <c>before</c> first, given the cluster the read
    /// starts at, and read nothing where it throws. Counts the reads in flight.
    /// </summary>
    private sealed class ScriptedClusterReads(IMftSource table, Func<long, CancellationToken, Task> before) : IMftSource
    {
        private int _inFlight;

        public int InFlight => Volatile.Read(ref _inFlight);

        public int BytesPerRecord => table.BytesPerRecord;

        public long RecordCount => table.RecordCount;

        public MftBitmapPlacement? Bitmap => table.Bitmap;

        public int BatchLength(long firstRecord, int capacity) => table.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination) => table.ReadBatch(firstRecord, destination);

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            table.ReadBatchAsync(firstRecord, destination, ct);

        public int BytesPerCluster => table.BytesPerCluster;

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => table.TryReadClusters(firstCluster, destination);

        public async ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct)
        {
            Interlocked.Increment(ref _inFlight);

            try
            {
                await before(firstCluster, ct).ConfigureAwait(false);
                return await table.TryReadClustersAsync(firstCluster, destination, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public void Dispose() => table.Dispose();
    }

    /// <summary>
    /// A table whose overlapped cluster reads each wait until <c>waitFor</c> of them are in flight, or
    /// half a second has passed, so reads made one at a time are slowed and seen to be rather than
    /// stuck. Records the cluster each started at, and the most in flight at once.
    /// </summary>
    private sealed class HeldClusterReads(IMftSource table, int waitFor) : IMftSource
    {
        private readonly TaskCompletionSource _enoughInFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _mostInFlight;

        public List<long> Clusters { get; } = [];

        public int MostInFlight => Volatile.Read(ref _mostInFlight);

        public int BytesPerRecord => table.BytesPerRecord;

        public long RecordCount => table.RecordCount;

        public MftBitmapPlacement? Bitmap => table.Bitmap;

        public int BatchLength(long firstRecord, int capacity) => table.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination) => table.ReadBatch(firstRecord, destination);

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            table.ReadBatchAsync(firstRecord, destination, ct);

        public int BytesPerCluster => table.BytesPerCluster;

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => table.TryReadClusters(firstCluster, destination);

        public async ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct)
        {
            lock (Clusters)
            {
                Clusters.Add(firstCluster);
            }

            var inFlight = Interlocked.Increment(ref _inFlight);

            lock (Clusters)
            {
                _mostInFlight = Math.Max(_mostInFlight, inFlight);
            }

            if (inFlight >= waitFor)
            {
                _enoughInFlight.TrySetResult();
            }

            await Task.WhenAny(_enoughInFlight.Task, Task.Delay(TimeSpan.FromMilliseconds(500), ct)).ConfigureAwait(false);

            var read = await table.TryReadClustersAsync(firstCluster, destination, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref _inFlight);
            return read;
        }

        public void Dispose() => table.Dispose();
    }
}
