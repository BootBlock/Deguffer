using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="ExploreScanner.ScanFoldersAsync"/> over folders on more than one volume: the volumes on
/// separate disks scanned at the same time, and those on one disk, or on a disk not known, in turn.
///
/// <para>Each table is opened through a factory that holds every open until another volume's open
/// arrives or <see cref="Company"/> passes, and counts the most that were held at once. Two at once
/// is only possible where two pipelines ran together.</para>
/// </summary>
public sealed class ScanFoldersOnSeveralDisksTests
{
    /// <summary>
    /// How long an open waits for another volume's. Long enough for a pipeline started at the same
    /// time to reach its open on a busy machine, and paid in full by each open that is rightly alone.
    /// </summary>
    private static readonly TimeSpan Company = TimeSpan.FromSeconds(1);

    private static MftFixture Photos()
    {
        var first = MftRecord.ReservedRecordCount;

        return new MftFixture()
            .AddDirectory(first, MftRecord.RootRecordNumber, "Photos")
            .AddFile(first + 1, first, "a.jpg", allocated: 4096, logical: 100);
    }

    private static ScanTuner Tuner(FakeStorageQueries storage, params string[] volumes)
    {
        var inventory = new FakeVolumeInventory();

        foreach (var volume in volumes)
        {
            inventory.With(volume);
        }

        return new ScanTuner(
            new FakePreferences(AppPreferences.Default with { Scanning = ScanPreferences.Default with { Route = ScanRoute.Table } }),
            new VolumeMediaCache(storage),
            inventory);
    }

    [Fact]
    public async Task VolumesOnSeparateDisksAreReadAtTheSameTime()
    {
        var sources = new CompanyMftSourceFactory(Photos(), 'X', 'Y');
        var tuner = Tuner(new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 2), @"X:\", @"Y:\");

        var scans = await new ExploreScanner(sources, tuning: tuner).ScanFoldersAsync([@"X:\Photos", @"Y:\Photos"]);

        Assert.Equal(2, sources.MostAtOnce);
        Assert.Equal([@"X:\Photos\a.jpg", @"Y:\Photos\a.jpg"], scans.Select(scan => scan.Tree.PathOf(Assert.Single(scan.Tree.ChildrenOf(scan.Node).ToArray()))));
        Assert.All(scans, scan => Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy));
    }

    /// <summary>Two volumes of one spinning disk read at once would make it seek between them for the length of both.</summary>
    [Fact]
    public async Task VolumesOnOneDiskAreReadInTurn()
    {
        var sources = new CompanyMftSourceFactory(Photos(), 'X', 'Y');
        var tuner = Tuner(new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 1), @"X:\", @"Y:\");

        await new ExploreScanner(sources, tuning: tuner).ScanFoldersAsync([@"X:\Photos", @"Y:\Photos"]);

        Assert.Equal(1, sources.MostAtOnce);
        Assert.Equal(2, sources.OpenCount);
    }

    /// <summary>A volume that would not say which disk it is on might be on the disk of any other.</summary>
    [Fact]
    public async Task AVolumeOnADiskNotKnownIsReadInTurnWithTheOthers()
    {
        var sources = new CompanyMftSourceFactory(Photos(), 'X', 'Y');
        var tuner = Tuner(new FakeStorageQueries().Volume(@"X:\", 1), @"X:\", @"Y:\");

        await new ExploreScanner(sources, tuning: tuner).ScanFoldersAsync([@"X:\Photos", @"Y:\Photos"]);

        Assert.Equal(1, sources.MostAtOnce);
    }

    /// <summary>
    /// The search has failed once one pipeline has, so the others are stopped rather than left to read
    /// whole volumes, and what is thrown is the failure, never the stop it caused.
    ///
    /// <para>The other pipeline is listed first, and its read waits on nothing but its token, so the
    /// scan ends only if the failure stops it.</para>
    /// </summary>
    [Fact]
    public async Task OnePipelinesFailureStopsTheOthersAndIsWhatIsThrown()
    {
        var sources = new FailingMftSourceFactory(Photos(), failing: 'Y', held: 'X');
        var tuner = Tuner(new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 2), @"X:\", @"Y:\");

        var scan = new ExploreScanner(sources, tuning: tuner).ScanFoldersAsync([@"X:\Photos", @"Y:\Photos"]).AsTask();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => scan.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(FailingMftSourceFactory.Failure, thrown.Message);
    }

    /// <summary>Opens that wait for each other's company, so two pipelines running at once show as two opens held at once.</summary>
    private sealed class CompanyMftSourceFactory(MftFixture fixture, params char[] letters) : IMftSourceFactory
    {
        private int _open;
        private int _mostAtOnce;
        private int _opened;

        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        public int OpenCount => Volatile.Read(ref _opened);

        public IMftSource? TryOpen(char driveLetter, out FallbackReason reason)
        {
            Interlocked.Increment(ref _opened);

            if (!letters.Contains(char.ToUpperInvariant(driveLetter)))
            {
                reason = FallbackReason.NotNtfsVolume;
                return null;
            }

            var atOnce = Interlocked.Increment(ref _open);
            InterlockedMax(ref _mostAtOnce, atOnce);

            SpinWait.SpinUntil(() => Volatile.Read(ref _open) > 1, Company);
            InterlockedMax(ref _mostAtOnce, Volatile.Read(ref _open));
            Interlocked.Decrement(ref _open);

            reason = FallbackReason.None;
            return fixture.Build();
        }

        private static void InterlockedMax(ref int most, int value)
        {
            var seen = Volatile.Read(ref most);

            while (value > seen && Interlocked.CompareExchange(ref most, value, seen) is var was && was != seen)
            {
                seen = was;
            }
        }
    }

    /// <summary>
    /// One volume whose open fails once the other's read has begun, and the other's read held until
    /// its token is cancelled.
    /// </summary>
    private sealed class FailingMftSourceFactory(MftFixture fixture, char failing, char held) : IMftSourceFactory
    {
        public const string Failure = "The volume driver failed.";

        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IMftSource? TryOpen(char driveLetter, out FallbackReason reason)
        {
            reason = FallbackReason.None;

            if (char.ToUpperInvariant(driveLetter) == failing)
            {
                _reading.Task.Wait(TimeSpan.FromSeconds(30));
                throw new InvalidOperationException(Failure);
            }

            return char.ToUpperInvariant(driveLetter) == held ? new Held(fixture.Build(), _reading) : null;
        }

        private sealed class Held(IMftSource inner, TaskCompletionSource reading) : IMftSource
        {
            public int BytesPerRecord => inner.BytesPerRecord;

            public long RecordCount => inner.RecordCount;

            public MftBitmapPlacement? Bitmap => inner.Bitmap;

            public int BytesPerCluster => inner.BytesPerCluster;

            public int BatchLength(long firstRecord, int capacity) => inner.BatchLength(firstRecord, capacity);

            public int ReadBatch(long firstRecord, Span<byte> destination) => inner.ReadBatch(firstRecord, destination);

            public async ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct)
            {
                reading.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return 0;
            }

            public bool TryReadClusters(long firstCluster, Span<byte> destination) => inner.TryReadClusters(firstCluster, destination);

            public ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct) =>
                inner.TryReadClustersAsync(firstCluster, destination, ct);

            public void Dispose() => inner.Dispose();
        }
    }
}
