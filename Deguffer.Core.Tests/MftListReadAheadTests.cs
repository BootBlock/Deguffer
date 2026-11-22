using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The attribute lists kept outside the table read beside the first pass rather than after it: sooner,
/// and with nothing else different.
/// </summary>
public sealed class MftListReadAheadTests
{
    private const string Root = @"C:\";

    /// <summary>Where a damaged table keeps a list the disk cannot read.</summary>
    private const long DamagedCluster = 777;

    /// <summary>
    /// A list's place is known once its record is parsed, so its read starts while the table is still
    /// being read. Here every read of the table past the lists' records is held until a list read
    /// starts, which a list read only after the first pass can never do.
    /// </summary>
    [Fact]
    public void StartsReadingAListBeforeTheFirstPassHasReadTheWholeTable()
    {
        using var table = new MftFixture()
            .AddFileWithANonResidentAttributeList(20, MftRecord.RootRecordNumber, "a.tgz", allocated: 4096, logical: 1000, extension: 30, listCluster: 700)
            .AddFile(200, MftRecord.RootRecordNumber, "last.txt", allocated: 4096, logical: 10)
            .WithoutBitmap()
            .Build();
        using var source = new HeldTableReads(table, heldFrom: 40);

        var handed = ReadAll(source, new TableTuning(TableTuning.MinimumReadBytes, 1, 1, 1, AttributeListReads.BesideFirstPass), out var wholeTable);

        Assert.True(source.ListReadBeforeTheTableEnded, "a list read started while the rest of the table waited for one");
        Assert.True(wholeTable);
        Assert.Equal(1000, handed[20].Record.Size?.Logical);
    }

    /// <summary>
    /// What every record holds, the files placed from the lists outside the table and those inside
    /// it, is the same whenever the lists are read, however many are read at once and on however many
    /// parse threads, and so are the tree drawn from it and the index measured from it. Damaged, the
    /// table also holds a list that cannot be read and a list with no place on the disk, and the index
    /// declines it either way.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandsOnTheSameRecordsWhicheverTimeTheListsAreRead(bool damaged)
    {
        using var table = ManyLists(damaged).Build();
        using var source = new HeldTableReads(table, heldFrom: long.MaxValue, unreadableCluster: damaged ? DamagedCluster : -1);
        var after = new TableTuning(TableTuning.MinimumReadBytes, 4, 1, 4);
        var handed = ReadAll(source, after, out var wholeTable);
        var expected = Described(handed);
        var tree = Drawn(source, after);
        var built = MftVolumeIndexBuilder.TryBuild(source, after, out var afterIndex);

        Assert.True(wholeTable);
        Assert.Equal(!damaged, built);
        Assert.Equal(damaged, handed.Any(pair => pair.Key >= MftRecord.ReservedRecordCount && pair.Value.Outcome is MftParseOutcome.Unreadable));

        foreach (var (listReadsInFlight, parseThreads) in new[] { (1, 1), (1, 4), (4, 4), (32, 2) })
        {
            var beside = new TableTuning(TableTuning.MinimumReadBytes, 4, parseThreads, listReadsInFlight, AttributeListReads.BesideFirstPass);

            Assert.Equal(expected, Described(ReadAll(source, beside, out var besideWhole)));
            Assert.True(besideWhole);
            Assert.Equal(tree, Drawn(source, beside));
            Assert.Equal(built, MftVolumeIndexBuilder.TryBuild(source, beside, out var index));
            Assert.Equal(afterIndex?.TryMeasure([]), index?.TryMeasure([]));
            Assert.Equal(afterIndex?.TryMeasure(["folder"]), index?.TryMeasure(["folder"]));
        }
    }

    /// <summary>
    /// The budget is spent in the order of the lists' clusters once the pass has ended, never in the
    /// order they were read beside it. Read beside the pass, 16's list lands first and alone, because
    /// the table's reads past it wait for its read, and 50's and 54's land after it, while their
    /// clusters run 50, 54, 16. The three want fewer records than the table holds, so reading beside
    /// the pass lands every one, but the lists inside 17 and 18 leave room for only one more, so 50 is
    /// followed, 54 spends the last of the budget and is followed too, and 16 is lost, as it is when
    /// every list is read after the pass.
    /// </summary>
    [Theory]
    [InlineData(AttributeListReads.AfterFirstPass)]
    [InlineData(AttributeListReads.BesideFirstPass)]
    public void SpendsTheBudgetInClusterOrderWhicheverOrderTheListsLanded(AttributeListReads listReads)
    {
        using var table = new MftFixture()
            .AddFileWhoseListOutsideTheTableNamesRecords(16, listCluster: 900, firstNamed: 1_000, count: 12)
            .AddFileWhoseListInsideTheRecordNamesRecords(17, firstNamed: 4_000, count: 20)
            .AddFileWhoseListInsideTheRecordNamesRecords(18, firstNamed: 5_000, count: 20)
            .AddFileWhoseListOutsideTheTableNamesRecords(50, listCluster: 700, firstNamed: 2_000, count: 12)
            .AddFileWhoseListOutsideTheTableNamesRecords(54, listCluster: 800, firstNamed: 3_000, count: 12)
            .WithoutBitmap()
            .Build();
        using var source = new HeldTableReads(table, heldFrom: listReads is AttributeListReads.BesideFirstPass ? 40 : long.MaxValue);

        // What is left once 17 and 18 want 40 records must hold one list of 12 and not two.
        Assert.True(source.RecordCount - 40 is >= 12 and < 24, $"the table holds {source.RecordCount} records");

        ReadAll(source, new TableTuning(TableTuning.MinimumReadBytes, 1, 1, 1, listReads), out var wholeTable);

        Assert.Equal(listReads is AttributeListReads.BesideFirstPass, source.ListReadBeforeTheTableEnded);
        Assert.False(wholeTable);
        Assert.Contains(source.RecordReads, r => r.First is >= 2_000 and < 2_012);
        Assert.Contains(source.RecordReads, r => r.First is >= 3_000 and < 3_012);
        Assert.DoesNotContain(source.RecordReads, r => r.First is >= 1_000 and < 1_012);
    }

    /// <summary>
    /// Lists read beside the pass that want more extension records than the table holds are a table
    /// NTFS did not write. Reading beside the pass stops there, so it holds no more of a hostile table
    /// than a read after the pass would, and the table is reported as not read whole either way.
    /// </summary>
    [Fact]
    public void StopsReadingBesideThePassOnceTheListsWantMoreThanTheTableHolds()
    {
        var fixture = new MftFixture();

        // Far enough apart that no two lists are read in one read.
        for (uint record = 16; record < 40; record++)
        {
            fixture.AddFileWhoseListOutsideTheTableNamesRecords(record, listCluster: record * 100, firstNamed: 1_000, count: 12);
        }

        using var table = fixture.Build();
        using var source = new HeldTableReads(table, heldFrom: long.MaxValue);

        Assert.True(source.RecordCount * 2 < 24 * 12, "the lists between them must want far more than the table holds");

        ReadAll(source, new TableTuning(TableTuning.MinimumReadBytes, 1, 1, 1, AttributeListReads.BesideFirstPass), out var wholeTable);

        Assert.False(wholeTable);

        // Read beside the pass until the lists landed wanted more than the table holds, and then
        // read again in cluster order until the budget was spent: never every list, let alone twice.
        Assert.True(source.ClusterReads < 24, $"{source.ClusterReads} lists were read");
    }

    /// <summary>
    /// A pass that fails while a list read beside it is in flight cancels that read and waits for it
    /// before the failure leaves, so no buffer is released while a read can still land in it.
    /// </summary>
    [Fact]
    public void WaitsForAListReadInFlightBeforeAFailedPassLeaves()
    {
        using var table = new MftFixture()
            .AddFileWithANonResidentAttributeList(20, MftRecord.RootRecordNumber, "a.tgz", allocated: 4096, logical: 1000, extension: 30, listCluster: 700)
            .AddFile(200, MftRecord.RootRecordNumber, "last.txt", allocated: 4096, logical: 10)
            .WithoutBitmap()
            .Build();
        using var source = new HeldTableReads(table, heldFrom: long.MaxValue, slowClusters: true);

        Assert.Throws<InvalidOperationException>(() => MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(TableTuning.MinimumReadBytes, 1, 1, 1, AttributeListReads.BesideFirstPass),
            (number, _, in _, _, _) =>
            {
                if (number == 200)
                {
                    Assert.True(SpinWait.SpinUntil(() => source.ClusterReadsInFlight > 0, TimeSpan.FromSeconds(10)), "the list read started");
                    throw new InvalidOperationException("The handler gave up.");
                }

                return true;
            },
            onProgress: null,
            default));

        Assert.True(source.ClusterReadCancelled, "the list read in flight was cancelled");
        Assert.Equal(0, source.ClusterReadsInFlight);
    }

    /// <summary>
    /// Files that defer: 120 with lists outside the table in clusters out of record order, and two
    /// whose data or name sits in an extension record named by a list inside their own record, with
    /// plain files and a folder between them. Where <paramref name="damaged"/>, one list is in
    /// <see cref="DamagedCluster"/> and one is sparse.
    /// </summary>
    private static MftFixture ManyLists(bool damaged)
    {
        var fixture = new MftFixture()
            .AddDirectory(16, MftRecord.RootRecordNumber, "folder")
            .AddFileWithDataInAnExtensionRecord(19, 16, "inside.bin", allocated: 8192, logical: 8000, extension: 900)
            .AddFileWithItsNameInAnExtensionRecord(20, 16, "named.dll", allocated: 8192, logical: 5000, extension: 901);

        if (damaged)
        {
            fixture.AddFileWithASparseAttributeList(17, 16, "sparse.bin", allocated: 4096, logical: 100, extension: 18);
        }

        for (uint i = 0; i < 120; i++)
        {
            var record = 30 + (i * 2);

            // Spread so that neither record order nor the order of reading batches is cluster order.
            var cluster = damaged && i == 7 ? DamagedCluster : 1_000 + ((i * 37) % 120 * 3);
            fixture.AddFileWithANonResidentAttributeList(
                record, i % 3 == 0 ? 16u : MftRecord.RootRecordNumber, $"file-{i}.bin", allocated: 4096 * (i + 1), logical: 4000 * (i + 1), extension: 1_000 + i, listCluster: cluster);
            fixture.AddFile(record + 1, 16, $"plain-{i}.txt", allocated: 4096, logical: i);
        }

        return fixture;
    }

    private static string[] Described(Dictionary<long, (MftParseOutcome Outcome, MftRecord Record, string Name)> handed) =>
        [.. handed.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}|{pair.Value.Outcome}|{pair.Value.Record}|{pair.Value.Name}")];

    private static string[] Drawn(IMftSource source, TableTuning tuning)
    {
        var tree = MftExploreReader.Read(source, Root, [], tuning, progress: null, default).Tree!;

        return [.. TreeNodes.Reachable(tree).Select(node => $"{tree.PathOf(node)}|{tree.SizeOf(node)}|{tree.LengthOf(node)}").Order(StringComparer.Ordinal)];
    }

    private static Dictionary<long, (MftParseOutcome Outcome, MftRecord Record, string Name)> ReadAll(
        IMftSource source, TableTuning tuning, out bool wholeTable)
    {
        var handed = new Dictionary<long, (MftParseOutcome Outcome, MftRecord Record, string Name)>();

        wholeTable = MftRecordStream.TryReadAll(source, (int)source.RecordCount, tuning, (number, outcome, in record, name, _) =>
        {
            lock (handed)
            {
                handed[number] = (outcome, record, name.ToString());
            }

            return true;
        }, onProgress: null, default);

        return handed;
    }

    /// <summary>
    /// A table whose overlapped reads of records from <c>heldFrom</c> on wait until a read of clusters
    /// has started, for up to ten seconds, and which records every read of clusters and every read of
    /// records made one at a time. A read of the cluster <c>unreadableCluster</c> fails. Where
    /// <c>slowClusters</c>, a read of clusters waits until it is cancelled, and lands a little after,
    /// as a real read does.
    /// </summary>
    private sealed class HeldTableReads(
        IMftSource table, long heldFrom, long unreadableCluster = -1, bool slowClusters = false) : IMftSource
    {
        private readonly TaskCompletionSource _listRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _clusterReads;
        private int _inFlight;
        private bool _released;

        public bool ListReadBeforeTheTableEnded { get; private set; }

        public bool ClusterReadCancelled { get; private set; }

        public int ClusterReads => Volatile.Read(ref _clusterReads);

        public int ClusterReadsInFlight => Volatile.Read(ref _inFlight);

        public List<(long First, int Records)> RecordReads { get; } = [];

        public int BytesPerRecord => table.BytesPerRecord;

        public long RecordCount => table.RecordCount;

        public MftBitmapPlacement? Bitmap => table.Bitmap;

        public int BytesPerCluster => table.BytesPerCluster;

        public int BatchLength(long firstRecord, int capacity) => table.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination)
        {
            lock (RecordReads)
            {
                RecordReads.Add((firstRecord, destination.Length / BytesPerRecord));
            }

            return table.ReadBatch(firstRecord, destination);
        }

        public async ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct)
        {
            if (firstRecord >= heldFrom && !_released)
            {
                ListReadBeforeTheTableEnded = await Task.WhenAny(_listRead.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)) == _listRead.Task;
                _released = true;
            }

            return await table.ReadBatchAsync(firstRecord, destination, ct);
        }

        public bool TryReadClusters(long firstCluster, Span<byte> destination) =>
            firstCluster != unreadableCluster && table.TryReadClusters(firstCluster, destination);

        public async ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct)
        {
            Interlocked.Increment(ref _clusterReads);
            Interlocked.Increment(ref _inFlight);
            _listRead.TrySetResult();

            try
            {
                if (slowClusters)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        ClusterReadCancelled = true;
                        await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);
                        throw;
                    }
                }

                return firstCluster != unreadableCluster && await table.TryReadClustersAsync(firstCluster, destination, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public void Dispose() => table.Dispose();
    }
}
