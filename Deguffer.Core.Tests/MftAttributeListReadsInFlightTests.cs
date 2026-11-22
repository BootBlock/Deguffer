using System.Buffers.Binary;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests;

/// <summary>
/// The attribute lists kept outside the table keep the disk asked for every read in flight whichever
/// read is slow, and are still handed on in cluster order whatever order their reads land in.
/// </summary>
public sealed class MftAttributeListReadsInFlightTests
{
    private const int ReadsInFlight = 4;

    /// <summary>How far past the oldest read not yet handed on the reader reads: four rounds of the reads in flight.</summary>
    private const int Window = ReadsInFlight * 4;

    private const int ClusterBytes = 4096;

    private const int ListBytes = 0x20;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// While the oldest read is slow, every read behind it lands and is replaced, so the disk is still
    /// asked for the reads in flight, up to the window and no further. A reader that waits for the
    /// oldest before it starts another makes only the first four.
    /// </summary>
    [Fact]
    public async Task StartsReadsPastTheOldestWhileItIsStillInFlight()
    {
        var clusters = Enumerable.Range(1, 30).Select(i => i * 1_000L).ToArray();
        using var source = new HeldReads(held: [1_000]);
        var handed = new List<long>();

        var reading = Task.Run(() => Read(source, Deferred(clusters), handed));

        try
        {
            for (var i = 0; i < Window; i++)
            {
                Assert.True(await source.Started.WaitAsync(Patience), $"read {i} was never started while the oldest was held");
            }
        }
        finally
        {
            source.Release(1_000, readable: true);
        }

        await reading.WaitAsync(Patience);

        Assert.Equal(clusters, handed);
        Assert.Equal(clusters, source.Reads.Select(r => r.Cluster).Order());
        Assert.All(source.Reads, read => Assert.True(
            Array.IndexOf(clusters, read.Cluster) < read.HandedBefore + Window,
            $"the read at {read.Cluster} was started with only {read.HandedBefore} lists handed on, past the window"));
        Assert.Equal(Window, source.Reads.Count(r => r.HandedBefore == 0));
    }

    /// <summary>
    /// Reads landing in the reverse of cluster order, among them one of several lists, still hand each
    /// list on in cluster order, each with the entries of its own clusters.
    /// </summary>
    [Fact]
    public async Task HandsListsOnInClusterOrderWhateverOrderTheReadsLandIn()
    {
        long[] clusters = [9_000, 700, 5_000, 702, 3_000, 701, 7_000];
        using var source = new HeldReads(held: [700, 3_000, 7_000]);
        var handed = new List<long>();

        var reading = Task.Run(() => Read(source, Deferred(clusters), handed));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(await source.Started.WaitAsync(Patience), "every read fits the window and must start at once");
        }

        source.Release(7_000, readable: true);
        source.Release(3_000, readable: true);
        source.Release(700, readable: true);

        await reading.WaitAsync(Patience);

        Assert.Equal([700, 701, 702, 3_000, 5_000, 7_000, 9_000], handed);
        Assert.Contains((700L, 3), source.Reads.Select(r => (r.Cluster, r.Clusters)));
    }

    /// <summary>
    /// A held read of several that then fails reads each of its lists again alone, so none of them is
    /// lost, while the read after it was started and handed on in its turn.
    /// </summary>
    [Fact]
    public async Task ReadsAHeldReadOfSeveralThatFailsAgainListByList()
    {
        long[] clusters = [700, 701, 702, 5_000];
        using var source = new HeldReads(held: [700]);
        var handed = new List<long>();

        var reading = Task.Run(() => Read(source, Deferred(clusters), handed));

        for (var i = 0; i < 2; i++)
        {
            Assert.True(await source.Started.WaitAsync(Patience), "the read after the held one must start while it is held");
        }

        source.Release(700, readable: false);
        await reading.WaitAsync(Patience);

        Assert.Equal(clusters, handed);
        Assert.Equal(
            [(700L, 3), (5_000L, 1), (700L, 1), (701L, 1), (702L, 1)],
            source.Reads.Select(r => (r.Cluster, r.Clusters)));
    }

    /// <summary>
    /// Cancelled while the oldest read is held, by a disk that ignores the cancellation, the reader hands
    /// nothing on, even once that read lands, and returns only after it has, so no read lands in a
    /// buffer it released. Once cancelled no read is made, so a reader that waited for the oldest to
    /// land before looking at the token would hand it on and then wait for a read never made.
    /// </summary>
    [Fact]
    public async Task CancelledWhileTheOldestIsHeldHandsNothingOnAndWaitsForIt()
    {
        var clusters = Enumerable.Range(1, 30).Select(i => i * 1_000L).ToArray();
        using var source = new HeldReads(held: [1_000], ignoreCancellation: true);
        using var cancel = new CancellationTokenSource();
        var handed = new List<long>();

        var reading = Task.Run(() => Read(source, Deferred(clusters), handed, cancel.Token));

        try
        {
            for (var i = 0; i < Window; i++)
            {
                Assert.True(await source.Started.WaitAsync(Patience), $"read {i} was never started while the oldest was held");
            }

            await cancel.CancelAsync();

            // Long enough for a reader that did not wait for the held read to have returned.
            await Task.WhenAny(reading, Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert.False(reading.IsCompleted, "the reader returned while the held read was still in flight");
        }
        finally
        {
            source.Release(1_000, readable: true);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Patience));
        Assert.Empty(handed);
    }

    /// <summary>
    /// Hand on every list, recording the cluster its one entry names, which is the cluster it was read
    /// from. A list handed on unread fails the test, since every list here can be read.
    /// </summary>
    private static void Read(
        HeldReads source, IReadOnlyList<MftDeferredRecord> deferred, List<long> handed, CancellationToken ct = default) =>
        MftAttributeListReader.Read(source, deferred, ReadsInFlight, (record, entries) =>
        {
            Assert.NotNull(entries);
            var entry = Assert.Single(entries);

            handed.Add(entry.Segment.Record);
            source.Handed();
            return true;
        }, ct);

    /// <summary>A record for each list, each list one cluster, given in the order of <paramref name="clusters"/>.</summary>
    private static List<MftDeferredRecord> Deferred(long[] clusters)
    {
        var deferred = new List<MftDeferredRecord>();

        for (var i = 0; i < clusters.Length; i++)
        {
            var record = new MftDeferredRecord(100 + i, 1, isDirectory: false, default, []);

            record.AwaitList([new DataRun(clusters[i], 1)], ListBytes);
            deferred.Add(record);
        }

        return deferred;
    }

    /// <summary>
    /// A volume whose every cluster holds a list of one entry naming that cluster. A read starting at a
    /// held cluster stays in flight until released, landing as readable or not, and until cancelled
    /// unless <c>ignoreCancellation</c>, as a disk that has the request already does. Each read is recorded
    /// with how many lists had been handed on when it was started.
    /// </summary>
    private sealed class HeldReads(long[] held, bool ignoreCancellation = false) : IMftSource
    {
        private readonly Dictionary<long, TaskCompletionSource<bool>> _held = held.ToDictionary(
            static c => c, static _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        private readonly List<(long Cluster, int Clusters, int HandedBefore)> _reads = [];
        private int _handed;

        public SemaphoreSlim Started { get; } = new(0);

        public IReadOnlyList<(long Cluster, int Clusters, int HandedBefore)> Reads
        {
            get
            {
                lock (_reads)
                {
                    return [.. _reads];
                }
            }
        }

        public int BytesPerRecord => 1024;

        public long RecordCount => 0;

        public MftBitmapPlacement? Bitmap => null;

        public int BytesPerCluster => ClusterBytes;

        public void Handed() => Interlocked.Increment(ref _handed);

        public void Release(long cluster, bool readable) => _held[cluster].SetResult(readable);

        public ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct)
        {
            var count = destination.Length / ClusterBytes;

            lock (_reads)
            {
                _reads.Add((firstCluster, count, Volatile.Read(ref _handed)));
            }

            Started.Release();

            for (var i = 0; i < count; i++)
            {
                var list = destination.Span.Slice(i * ClusterBytes, ListBytes);

                list.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(list, 0x80);
                BinaryPrimitives.WriteUInt16LittleEndian(list[0x04..], ListBytes);
                list[0x07] = 0x1A;
                BinaryPrimitives.WriteUInt64LittleEndian(list[0x10..], (ulong)(firstCluster + i));
            }

            // Only the first read of a held cluster is held: one made again alone reads at once.
            if (_held.TryGetValue(firstCluster, out var hold) && !hold.Task.IsCompleted)
            {
                return new ValueTask<bool>(ignoreCancellation ? hold.Task : hold.Task.WaitAsync(ct));
            }

            return ValueTask.FromResult(true);
        }

        public int ReadBatch(long firstRecord, Span<byte> destination) => throw new NotSupportedException();

        public int BatchLength(long firstRecord, int capacity) => throw new NotSupportedException();

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            throw new NotSupportedException();

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => throw new NotSupportedException();

        public void Dispose() => Started.Dispose();
    }
}
