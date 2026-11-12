using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The attribute lists kept outside the table are read together where their clusters are close,
/// and every list is still handed on as it would be read alone.
/// </summary>
public sealed class MftAttributeListReaderTests
{
    private static readonly TableTuning Tuning = new(TableTuning.MaximumReadBytes, readsInFlight: 4, parseThreads: 1, listReadsInFlight: 4);

    /// <summary>
    /// About half the lists on a real volume sit in the cluster after the one before, and a read each
    /// costs the disk a request for every cluster. Lists whose clusters touch are one read, a list
    /// far from them another, and every file still gets its size from its own list.
    /// </summary>
    [Fact]
    public void ReadsListsWhoseClustersTouchInOneRead()
    {
        using var table = Lists(700, 701, 702, 950).Build();
        using var source = new ListReads(table);

        var sizes = Sizes(source);

        Assert.Equal([(700L, 3), (950L, 1)], source.Reads);
        Assert.Equal(Expected(700, 701, 702, 950), sizes);
    }

    /// <summary>
    /// A short run of clusters between two lists is read across, as the passes over the table read
    /// across a short run of records, and a longer run ends the read.
    /// </summary>
    [Fact]
    public void ReadsAcrossAShortGapBetweenListsButNotALongOne()
    {
        const int gap = MftReadGap.Bytes / MftRecordBytes.BytesPerCluster;
        using var table = Lists(700, 701 + gap, 702 + gap + gap + 1).Build();
        using var source = new ListReads(table, blankUnplaced: true);

        var sizes = Sizes(source);

        Assert.Equal([(700L, gap + 2), (702L + gap + gap + 1, 1)], source.Reads);
        Assert.Equal(Expected(700, 701 + gap, 702 + gap + gap + 1), sizes);
    }

    /// <summary>
    /// A read of several that fails is made again for each list alone, so a cluster that cannot be read
    /// loses only the list in it, as it did when every list was read alone.
    /// </summary>
    [Fact]
    public void LosesOnlyTheListInAClusterThatCannotBeRead()
    {
        using var table = Lists(700, 701, 702).Build();
        using var source = new ListReads(table, refused: 701);

        var sizes = Sizes(source);

        Assert.Equal([(700L, 3), (700L, 1), (701L, 1), (702L, 1)], source.Reads);
        Assert.Equal(
            new Dictionary<long, (MftParseOutcome, long?)>
            {
                [Owner(700)] = (MftParseOutcome.Parsed, Logical(700)),
                [Owner(701)] = (MftParseOutcome.Unreadable, null),
                [Owner(702)] = (MftParseOutcome.Parsed, Logical(702)),
            },
            sizes);
    }

    /// <summary>
    /// A gap read across is not part of any list, so a cluster in it that cannot be read loses no list:
    /// each is read again alone. The fixture serves no cluster it did not place, so the gap here fails.
    /// </summary>
    [Fact]
    public void LosesNoListToAGapThatCannotBeRead()
    {
        using var table = Lists(700, 705).Build();
        using var source = new ListReads(table);

        var sizes = Sizes(source);

        Assert.Equal([(700L, 6), (700L, 1), (705L, 1)], source.Reads);
        Assert.Equal(Expected(700, 705), sizes);
    }

    /// <summary>
    /// Lists read together are handed on in cluster order and stop where the budget runs out, so a
    /// table whose lists want more than it holds loses the same files as when each was read alone:
    /// the first two lists in cluster order are followed, and the third, read with them, is not.
    /// </summary>
    [Fact]
    public void StopsHandingOnListsReadTogetherWhereTheBudgetRunsOut()
    {
        using var table = new MftFixture()
            .AddFileWhoseListOutsideTheTableNamesRecords(16, listCluster: 702, firstNamed: 1_000, count: 12)
            .AddFileWhoseListOutsideTheTableNamesRecords(17, listCluster: 700, firstNamed: 2_000, count: 12)
            .AddFileWhoseListOutsideTheTableNamesRecords(18, listCluster: 701, firstNamed: 3_000, count: 12)
            .Build();
        using var source = new ListReads(table);

        Assert.True(source.RecordCount < 24, "two lists must want more than the table holds, and one must not");

        var whole = MftRecordStream.TryReadAll(source, (int)source.RecordCount, Tuning, static (_, _, in _, _, _) => true, onProgress: null, default);

        Assert.False(whole);
        Assert.Equal([(700L, 3)], source.Reads);
        Assert.Contains(source.RecordReads, r => r is >= 2_000 and < 2_012);
        Assert.Contains(source.RecordReads, r => r is >= 3_000 and < 3_012);
        Assert.DoesNotContain(source.RecordReads, r => r is >= 1_000 and < 1_012);
    }

    /// <summary>A table with a file whose list is kept in each of <paramref name="clusters"/>, each list naming an extension record of its own.</summary>
    private static MftFixture Lists(params long[] clusters)
    {
        var fixture = new MftFixture();

        foreach (var cluster in clusters)
        {
            fixture.AddFileWithANonResidentAttributeList(
                Owner(cluster), MftRecord.RootRecordNumber, $"{cluster}.tgz", allocated: 1 << 20, logical: Logical(cluster), Owner(cluster) + 100, cluster);
        }

        return fixture;
    }

    /// <summary>The record of the file whose list is in <paramref name="cluster"/>.</summary>
    private static uint Owner(long cluster) => (uint)(cluster % 100) + 20;

    /// <summary>The size of the file whose list is in <paramref name="cluster"/>, different for every list.</summary>
    private static long Logical(long cluster) => 1_000 + cluster;

    private static Dictionary<long, (MftParseOutcome, long?)> Expected(params long[] clusters) =>
        clusters.ToDictionary(c => (long)Owner(c), c => (MftParseOutcome.Parsed, (long?)Logical(c)));

    /// <summary>
    /// How each file with a list outside the table was handed on, and its size, by its record. A file
    /// is told from the extension records its list names by its number, which is below theirs.
    /// </summary>
    private static Dictionary<long, (MftParseOutcome, long?)> Sizes(IMftSource source)
    {
        var sizes = new Dictionary<long, (MftParseOutcome, long?)>();

        MftRecordStream.TryReadAll(source, (int)source.RecordCount, Tuning, (number, outcome, in record, _, _) =>
        {
            if (number is >= 20 and < 120)
            {
                lock (sizes)
                {
                    sizes[number] = (outcome, record.Size?.Logical);
                }
            }

            return true;
        }, onProgress: null, default);

        return sizes;
    }

    /// <summary>
    /// A table that records each overlapped read of clusters by where it starts and how many clusters
    /// it asks for, and each read of records made one at a time by where it starts. A read reaching
    /// <c>refused</c> fails. Where <c>blankUnplaced</c>, a cluster the fixture did not place reads as
    /// zeroes, as free space between two lists on a real volume does.
    /// </summary>
    private sealed class ListReads(IMftSource table, long? refused = null, bool blankUnplaced = false) : IMftSource
    {
        private readonly List<(long Cluster, int Clusters)> _reads = [];

        public IReadOnlyList<(long Cluster, int Clusters)> Reads
        {
            get
            {
                lock (_reads)
                {
                    return [.. _reads];
                }
            }
        }

        public List<long> RecordReads { get; } = [];

        public int BytesPerRecord => table.BytesPerRecord;

        public long RecordCount => table.RecordCount;

        public MftBitmapPlacement? Bitmap => table.Bitmap;

        public int BatchLength(long firstRecord, int capacity) => table.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination)
        {
            RecordReads.Add(firstRecord);
            return table.ReadBatch(firstRecord, destination);
        }

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            table.ReadBatchAsync(firstRecord, destination, ct);

        public int BytesPerCluster => table.BytesPerCluster;

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => table.TryReadClusters(firstCluster, destination);

        public ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct)
        {
            var count = destination.Length / BytesPerCluster;

            lock (_reads)
            {
                _reads.Add((firstCluster, count));
            }

            if (refused is { } bad && bad >= firstCluster && bad < firstCluster + count)
            {
                return ValueTask.FromResult(false);
            }

            for (var i = 0; i < count; i++)
            {
                var cluster = destination.Span.Slice(i * BytesPerCluster, BytesPerCluster);

                if (!table.TryReadClusters(firstCluster + i, cluster))
                {
                    if (!blankUnplaced)
                    {
                        return ValueTask.FromResult(false);
                    }

                    cluster.Clear();
                }
            }

            return ValueTask.FromResult(true);
        }

        public void Dispose() => table.Dispose();
    }
}
