using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A pass with several reads in flight and several threads parsing, held to every rule a pass that
/// read and parsed one batch at a time kept: records are numbered by position, a short read is never
/// skipped past, and nothing is released while a read can still land in it.
/// </summary>
public sealed class MftReadPassTests
{
    private const uint Folder = 16;

    /// <summary>The smallest reads, one at a time on one thread, and the largest of everything.</summary>
    public static TheoryData<int, int, int> Tunings => new()
    {
        { TableTuning.MinimumReadBytes, TableTuning.MinimumReadsInFlight, TableTuning.MinimumParseThreads },
        { TableTuning.MinimumReadBytes, TableTuning.MaximumReadsInFlight, TableTuning.MaximumParseThreads },
        { TableTuning.MaximumReadBytes, TableTuning.MaximumReadsInFlight, TableTuning.MaximumParseThreads },
        { TableTuning.MinimumReadBytes, 4, 3 },
    };

    /// <summary>
    /// Settings change speed and never results. The same table read with the smallest and largest
    /// of every value, with its bitmap and without, hands on the same records with the same
    /// contents, and builds an index that measures the same.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tunings))]
    public void HandsOnTheSameRecordsWhateverTheValues(int readBytes, int readsInFlight, int parseThreads)
    {
        var tuning = new TableTuning(readBytes, readsInFlight, parseThreads);
        var expected = Handed(LargeTable().WithoutBitmap(), TableTuning.Default);

        foreach (var withBitmap in new[] { false, true })
        {
            var fixture = withBitmap ? LargeTable() : LargeTable().WithoutBitmap();
            var handed = Handed(fixture, tuning);

            // A free record is handed on only where there is no bitmap to say it is free.
            Assert.Equal(
                withBitmap ? expected.Where(r => r.Value.Outcome != MftParseOutcome.NotAnEntry) : expected,
                withBitmap ? handed.Where(r => r.Value.Outcome != MftParseOutcome.NotAnEntry) : handed);

            using var source = fixture.Build();
            Assert.True(MftVolumeIndexBuilder.TryBuild(source, tuning, out var index));

            // The 280 files that are not mail stores and the one in the folder inside: a store is
            // in no total.
            Assert.Equal((281L * 4096, 281L * 4000), Sizes(index.TryMeasure(["folder"])));
            Assert.Equal((4096L, 4000L), Sizes(index.TryMeasure(["folder", "deep"])));

            index.TryMeasure(["folder"], MinimumAge.Off, out _, out var stores);
            Assert.Equal(20, stores.Count);
        }
    }

    /// <summary>
    /// Reads complete in whatever order the disk finishes them. Here the earlier a read starts the
    /// later it completes, and every record is still the record its position says.
    /// </summary>
    [Fact]
    public void NumbersEachRecordByItsPositionWhateverOrderReadsComplete()
    {
        var completed = new ConcurrentQueue<long>();

        using var source = new ScriptedReads(LargeTable().WithoutBitmap().Build(), async (inner, first, destination, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, 40 - (first / 64))), ct);
            completed.Enqueue(first);
            return inner.ReadBatch(first, destination.Span);
        });

        var handed = Handed(source, new TableTuning(TableTuning.MinimumReadBytes, 8, 4));

        Assert.Equal(Handed(LargeTable().WithoutBitmap(), TableTuning.Default), handed);

        var order = completed.ToList();
        Assert.True(order.Zip(order.Skip(1)).Any(pair => pair.First > pair.Second), "the reads must complete out of order");
    }

    /// <summary>
    /// A read that brings fewer records than it asked for is read again from where it stopped, into
    /// the same buffer, until the rest arrives. Every record arrives once.
    /// </summary>
    [Fact]
    public void ReadsTheRestOfAShortReadRatherThanSkippingIt()
    {
        var shortened = new ConcurrentDictionary<long, bool>();

        using var source = new ScriptedReads(LargeTable().WithoutBitmap().Build(), (inner, first, destination, ct) =>
        {
            var records = destination.Length / inner.BytesPerRecord;

            // Half of what was asked, the first time each place is read.
            if (records > 1 && shortened.TryAdd(first, true))
            {
                destination = destination[..(records / 2 * inner.BytesPerRecord)];
            }

            return Task.FromResult(inner.ReadBatch(first, destination.Span));
        });

        var handed = new ConcurrentBag<long>();

        Assert.True(MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(TableTuning.MinimumReadBytes, 4, 2),
            (number, _, in _) =>
            {
                handed.Add(number);
                return true;
            },
            onProgress: null,
            default));

        Assert.Equal(Enumerable.Range(0, (int)source.RecordCount).Select(n => (long)n), handed.Order());
        Assert.Contains(shortened.Keys, first => first > 0);
    }

    /// <summary>
    /// A region that cannot be read ends the pass and says so, and every record before it is still
    /// handed on, though the reads after it were in flight beside it.
    /// </summary>
    [Fact]
    public void HandsOnEveryRecordBeforeARegionThatCannotBeRead()
    {
        using var source = LargeTable().UnreadableFrom(1_500).Build();
        var handed = new ConcurrentBag<long>();

        Assert.False(MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(TableTuning.MinimumReadBytes, 8, 4),
            (number, outcome, in _) =>
            {
                if (outcome == MftParseOutcome.Parsed)
                {
                    handed.Add(number);
                }

                return true;
            },
            onProgress: null,
            default));

        var inUseBefore = Handed(LargeTable(), TableTuning.Default)
            .Where(r => r.Key < 1_500 && r.Value.Outcome == MftParseOutcome.Parsed)
            .Select(r => r.Key);

        Assert.Equal(inUseBefore, handed.Where(n => n < 1_500).Order());
        Assert.DoesNotContain(handed, n => n >= 1_500);
    }

    /// <summary>
    /// A region that cannot be read stops the pass there even where the table can be read again
    /// after it. Reads already in flight past it still land, and what they bring is not handed on,
    /// and no read is started after them.
    ///
    /// <para>The read covering record 1,500 brings nothing, at once. Every read after it takes a
    /// while, so each is still in flight when the region is found.</para>
    /// </summary>
    [Fact]
    public void StopsAtARegionThatCannotBeReadThoughTheTableGoesOn()
    {
        const int ReadsInFlight = 4;
        var startedPast = 0;

        using var source = new ScriptedReads(LargeTable().WithoutBitmap().Build(), async (inner, first, destination, ct) =>
        {
            var records = destination.Length / inner.BytesPerRecord;

            if (first <= 1_500 && 1_500 < first + records)
            {
                return 0;
            }

            if (first > 1_500)
            {
                Interlocked.Increment(ref startedPast);
                await Task.Delay(100, ct);
            }

            return inner.ReadBatch(first, destination.Span);
        });

        var handed = new ConcurrentBag<long>();

        Assert.False(MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(64 * 1024, ReadsInFlight, 2),
            (number, _, in _) =>
            {
                handed.Add(number);
                return true;
            },
            onProgress: null,
            default));

        // Reads of 64 records, so the one that brings nothing starts at 1,472.
        Assert.Equal(Enumerable.Range(0, 1_472).Select(n => (long)n), handed.Order());
        Assert.InRange(startedPast, 1, ReadsInFlight);
    }

    /// <summary>
    /// A record whose bit is clear is never read or handed on, even where its header says it is in
    /// use: a file created after the bitmap was read. A record whose bit is set but whose header says
    /// it is free is handed on as free: the header decides what a record is.
    /// </summary>
    [Fact]
    public void BelievesAClearBitAndLetsTheHeaderDecideASetOne()
    {
        var fixture = LargeTable().MarkFree(600).MarkInUse(2_999);
        var handed = Handed(fixture, TableTuning.Default);

        Assert.DoesNotContain(600L, handed.Keys);
        Assert.Equal(MftParseOutcome.NotAnEntry, handed[2_999].Outcome);
    }

    /// <summary>A run of free records longer than a read is not read at all.</summary>
    [Fact]
    public void NeverReadsALongRunOfFreeRecords()
    {
        var reads = new ConcurrentBag<(long First, int Records)>();

        using var source = new ScriptedReads(LargeTable().Build(), (inner, first, destination, ct) =>
        {
            reads.Add((first, destination.Length / inner.BytesPerRecord));
            return Task.FromResult(inner.ReadBatch(first, destination.Span));
        });

        Handed(source, new TableTuning(64 * 1024, 4, 2));

        // The table is free from 2,000 to 2,998, far more than one read of 64 records.
        Assert.DoesNotContain(reads, read => read.First < 2_900 && read.First + read.Records > 2_100);
    }

    /// <summary>
    /// A cancelled pass stops promptly, and only once every read in flight has completed: a read
    /// lands in its buffer whenever the disk finishes, so a buffer released before then would be
    /// written after it was freed.
    /// </summary>
    [Fact]
    public void ACancelledPassReturnsOnlyOnceNoReadIsInFlight()
    {
        var inFlight = 0;
        using var cancel = new CancellationTokenSource();

        using var source = new ScriptedReads(LargeTable().Build(), async (inner, first, destination, ct) =>
        {
            Interlocked.Increment(ref inFlight);

            try
            {
                if (first > 0)
                {
                    await cancel.CancelAsync();
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return inner.ReadBatch(first, destination.Span);
            }
            finally
            {
                // Late, as a disk completing a cancelled read is.
                await Task.Delay(20, CancellationToken.None);
                Interlocked.Decrement(ref inFlight);
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => Handed(source, new TableTuning(TableTuning.MinimumReadBytes, 8, 4), cancel.Token));
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    /// <summary>
    /// A read the volume refuses is thrown as it was raised, once every other read has completed,
    /// so the caller's fallback for a refused read still sees one.
    /// </summary>
    [Fact]
    public void ThrowsARefusedReadUnchangedOnceNoReadIsInFlight()
    {
        var inFlight = 0;

        using var source = new ScriptedReads(LargeTable().Build(), async (inner, first, destination, ct) =>
        {
            Interlocked.Increment(ref inFlight);

            try
            {
                await Task.Yield();

                return first >= 1_000
                    ? throw new IOException("The parameter is incorrect.")
                    : inner.ReadBatch(first, destination.Span);
            }
            finally
            {
                await Task.Delay(10, CancellationToken.None);
                Interlocked.Decrement(ref inFlight);
            }
        });

        Assert.Throws<IOException>(() => Handed(source, new TableTuning(TableTuning.MinimumReadBytes, 8, 4)));
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    /// <summary>What the handler throws reaches the caller unchanged, from whichever thread it was on.</summary>
    [Fact]
    public void ThrowsWhatTheHandlerThrewUnchanged()
    {
        using var source = LargeTable().Build();

        Assert.Throws<InvalidOperationException>(() => MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(TableTuning.MinimumReadBytes, 8, 4),
            (number, _, in _) => number == 1_202 ? throw new InvalidOperationException() : true,
            onProgress: null,
            default));
    }

    /// <summary>A handler that asks to stop stops the pass, which then reads no further.</summary>
    [Fact]
    public void StopsWhereTheHandlerAsksTo()
    {
        using var source = LargeTable().WithoutBitmap().Build();
        var handed = 0;

        Assert.False(MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(TableTuning.MinimumReadBytes, 2, 1),
            (number, _, in _) =>
            {
                Interlocked.Increment(ref handed);
                return number != 100;
            },
            onProgress: null,
            default));

        Assert.InRange(handed, 101, source.RecordCount - 1);
    }

    /// <summary>
    /// A read already in flight when the handler asks to stop is one a pass one at a time would never
    /// have made, so its failing is not the pass failing: the pass still ends as abandoned rather
    /// than throwing. The reads past the first are held until the handler has asked to stop, and a
    /// moment more, then fail as a bad sector would.
    /// </summary>
    [Fact]
    public void AReadThatFailsAfterTheHandlerAskedToStopIsNotAFailure()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var source = new ScriptedReads(LargeTable().WithoutBitmap().Build(), async (inner, first, destination, ct) =>
        {
            if (first == 0)
            {
                return inner.ReadBatch(first, destination.Span);
            }

            await stopped.Task;
            throw new IOException("bad sector");
        });

        Assert.False(MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(64 * 1024, 4, 1),
            (number, _, in _) =>
            {
                if (number != 20)
                {
                    return true;
                }

                _ = Task.Delay(50).ContinueWith(_ => stopped.TrySetResult(), TaskScheduler.Default);
                return false;
            },
            onProgress: null,
            default));
    }

    /// <summary>
    /// Progress is reported from whichever thread finishes a read, but never by two at once and never
    /// going back, so a caller's progress closure needs no lock of its own.
    /// </summary>
    [Fact]
    public void ReportsProgressOneAtATimeAndOnlyForward()
    {
        var fixture = LargeTable();

        for (uint i = 3_000; i < 140_000; i += 997)
        {
            fixture.AddFile(i, Folder, $"late-{i}.bin", allocated: 4096, logical: 4000);
        }

        using var source = fixture.WithoutBitmap().Build();
        var reporting = 0;
        var overlapped = false;
        var reports = new List<long>();

        MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(TableTuning.MinimumReadBytes, TableTuning.MaximumReadsInFlight, TableTuning.MaximumParseThreads),
            static (_, _, in _) => true,
            done =>
            {
                overlapped |= Interlocked.Increment(ref reporting) > 1;
                reports.Add(done);
                Thread.Sleep(1);
                Interlocked.Decrement(ref reporting);
            },
            default);

        Assert.False(overlapped);
        Assert.Equal(reports.Order(), reports);
        Assert.True(reports.Count > 2);
        Assert.InRange(reports[^1], source.RecordCount - MftPassProgress.Interval + 1, source.RecordCount);
    }

    /// <summary>
    /// The calling thread waits while reads are in flight, and only a read arriving, a parse thread
    /// ending, a failure or a cancellation wakes it. The thread that finds the end of the table, or
    /// gives back the last buffer, may be any of them. Reads complete on pool threads after a little
    /// work, so many orders are tried over many passes, with one read in flight and one parse thread
    /// so that a single missed wake-up is enough to hang.
    /// </summary>
    [Fact]
    public async Task EndsWhicheverThreadFindsTheEndOfTheTable()
    {
        var table = LargeTable();

        for (var pass = 0; pass < 300; pass++)
        {
            var seed = pass;
            using var source = new ScriptedReads(table.Build(), async (inner, first, destination, ct) =>
            {
                await Task.Yield();
                Thread.SpinWait((int)((first * 7919 + seed) % 2000));
                return inner.ReadBatch(first, destination.Span);
            });

            var run = Task.Run(() => Handed(source, new TableTuning(64 * 1024, 1, 1)));

            Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
            Assert.Contains(3_000L, (await run).Keys);
        }
    }

    /// <summary>
    /// The progress callback is the caller's, and may throw on whichever thread counts the free
    /// records a read skips, a thread a read completed on included. What it throws is thrown
    /// unchanged, and only once no read is in flight.
    /// </summary>
    [Fact]
    public void ThrowsWhatTheProgressCallbackThrewOnceNoReadIsInFlight()
    {
        var inFlight = 0;
        var fixture = new MftFixture()
            .AddDirectory(Folder, MftRecord.RootRecordNumber, "folder")
            .AddFile(20, Folder, "first.bin", allocated: 4096, logical: 4000)
            .AddUnused(70_000)
            .AddFile(70_100, Folder, "after.bin", allocated: 4096, logical: 4000);

        for (uint i = 70_200; i < 72_000; i += 7)
        {
            fixture.AddFile(i, Folder, $"late-{i}.bin", allocated: 4096, logical: 4000);
        }

        using var source = new ScriptedReads(fixture.Build(), async (inner, first, destination, ct) =>
        {
            Interlocked.Increment(ref inFlight);

            try
            {
                await Task.Delay(5, ct);
                return inner.ReadBatch(first, destination.Span);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        });

        var thrown = Assert.Throws<InvalidOperationException>(() => MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(64 * 1024, 4, 2),
            static (_, _, in _) => true,
            done =>
            {
                if (done > 0)
                {
                    throw new InvalidOperationException("progress");
                }
            },
            default));

        Assert.Equal("progress", thrown.Message);
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    /// <summary>
    /// Records are parsed on the calling thread and on up to as many more at once as the parse
    /// threads allow, never more. The handler takes a moment over each record, so reads arrive
    /// faster than one thread parses them and a second thread has work.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ParsesOnAsManyThreadsAtOnceAsAllowed(int parseThreads)
    {
        using var source = LargeTable().Build();
        var caller = Environment.CurrentManagedThreadId;
        var threads = new ConcurrentDictionary<int, bool>();
        var parsing = 0;
        var mostAtOnce = 0;

        MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(TableTuning.MinimumReadBytes, 8, parseThreads),
            (_, _, in _) =>
            {
                threads.TryAdd(Environment.CurrentManagedThreadId, true);

                var now = Interlocked.Increment(ref parsing);
                for (var most = Volatile.Read(ref mostAtOnce); now > most; most = Volatile.Read(ref mostAtOnce))
                {
                    Interlocked.CompareExchange(ref mostAtOnce, now, most);
                }

                Thread.SpinWait(50_000);
                Interlocked.Decrement(ref parsing);
                return true;
            },
            onProgress: null,
            default);

        Assert.Contains(caller, threads.Keys);

        if (parseThreads == 1)
        {
            Assert.Single(threads);
        }
        else
        {
            Assert.InRange(mostAtOnce, 2, parseThreads);
        }
    }

    /// <summary>
    /// A folder of 300 files, 20 of them mail stores, a folder inside it, and free records between,
    /// at the start and in a long run before a last file at 3,000: several reads at the smallest size,
    /// and one at the largest.
    /// </summary>
    private static MftFixture LargeTable()
    {
        var fixture = new MftFixture().AddDirectory(Folder, MftRecord.RootRecordNumber, "folder");

        for (uint i = 0; i < 300; i++)
        {
            var name = i % 15 == 0 ? $"mail-{i}.pst" : $"file-{i}.bin";
            fixture.AddFile(20 + (i * 6), Folder, name, allocated: 4096, logical: 4000);
        }

        return fixture
            .AddDirectory(1_900, Folder, "deep")
            .AddFile(1_950, 1_900, "inner.bin", allocated: 4096, logical: 4000)
            .AddFile(3_000, MftRecord.RootRecordNumber, "last.bin", allocated: 4096, logical: 4000);
    }

    private static (long Allocated, long Logical) Sizes(ScanSize? size) =>
        (size!.Value.Allocated, size.Value.Logical);

    private static SortedDictionary<long, (MftParseOutcome Outcome, MftRecord Record)> Handed(MftFixture fixture, TableTuning tuning)
    {
        using var source = fixture.Build();
        return Handed(source, tuning);
    }

    private static SortedDictionary<long, (MftParseOutcome Outcome, MftRecord Record)> Handed(
        IMftSource source, TableTuning tuning, CancellationToken ct = default)
    {
        var handed = new ConcurrentDictionary<long, (MftParseOutcome, MftRecord)>();

        MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            tuning,
            (number, outcome, in record) =>
            {
                Assert.True(handed.TryAdd(number, (outcome, outcome == MftParseOutcome.Parsed ? record : default)), $"record {number} was handed on twice");
                return true;
            },
            onProgress: null,
            ct);

        return new SortedDictionary<long, (MftParseOutcome, MftRecord)>(handed);
    }

    /// <summary>A fixture table whose overlapped reads are made by <paramref name="read"/>.</summary>
    private sealed class ScriptedReads(
        IMftSource inner, Func<IMftSource, long, Memory<byte>, CancellationToken, Task<int>> read) : IMftSource
    {
        public int BytesPerRecord => inner.BytesPerRecord;

        public long RecordCount => inner.RecordCount;

        public MftBitmapPlacement? Bitmap => inner.Bitmap;

        public int BatchLength(long firstRecord, int capacity) => inner.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination) => inner.ReadBatch(firstRecord, destination);

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            new(read(inner, firstRecord, destination, ct));

        public int BytesPerCluster => inner.BytesPerCluster;

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => inner.TryReadClusters(firstCluster, destination);

        public void Dispose() => inner.Dispose();
    }
}
