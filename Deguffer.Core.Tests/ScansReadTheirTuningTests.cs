using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every scan asks for the values of the drive it reads as it starts, so a setting reaches each of
/// them. The table's read size is seen directly, in the size of the reads the source is handed. Each
/// walk hands its tuner to the walk itself, which resolves the values, so each walk is shown to ask
/// about its drive and the walk is shown to run with the thread count it resolved. The table's reads
/// in flight are seen as the most reads the source is waiting on at once.
/// </summary>
public sealed class ScansReadTheirTuningTests : IDisposable
{
    private const byte Nvme = 0x11;
    private const int ReadKiB = 8;
    private const int ReadsInFlight = 3;

    private readonly TempDirectory _temp = new();
    private readonly FakeStorageQueries _queries;
    private readonly ScanTuner _tuner;

    public ScansReadTheirTuningTests()
    {
        var root = Path.GetPathRoot(_temp.Path)!;

        _queries = new FakeStorageQueries().Volume(root, 1).Disk(1, Nvme, seekPenalty: false);
        _tuner = new ScanTuner(
            new FakePreferences(AppPreferences.Default with
            {
                Scanning = ScanPreferences.Default.With(
                    StorageMedia.Nvme, new MediaScanPreferences(TableReadKiB: ReadKiB, TableReadsInFlight: ReadsInFlight)),
            }),
            new VolumeMediaCache(_queries),
            new FakeVolumeInventory().With(root));
    }

    public void Dispose() => _temp.Dispose();

    private string Folder()
    {
        _temp.CreateFile(10, "cache", "a.bin");
        return Path.Combine(_temp.Path, "cache");
    }

    [Fact]
    public async Task TheFallbackWalkAsksAboutItsDrive()
    {
        await new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), tuning: _tuner)
            .MeasureAsync(Folder());

        Assert.Equal(1, _queries.ExtentsAsked);
    }

    [Fact]
    public async Task TheHardLinkWalkAsksAboutItsDrive()
    {
        await new HardLinkAwareScanner(_tuner).MeasureAsync(Folder());

        Assert.Equal(1, _queries.ExtentsAsked);
    }

    [Fact]
    public async Task ExplorersWalkAsksAboutItsDrive()
    {
        await new ExploreScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), tuning: _tuner)
            .ScanAsync(Folder());

        Assert.Equal(1, _queries.ExtentsAsked);
    }

    [Fact]
    public void TheChromiumWalkAsksAboutItsDrive()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        Directory.CreateDirectory(environment.RoamingAppData);
        Directory.CreateDirectory(environment.LocalAppData);

        new ChromiumUserDataDiscovery(environment, _tuner).Discover();

        Assert.Equal(1, _queries.ExtentsAsked);
    }

    /// <summary>
    /// Every scan walks through the overload that takes the tuner, so the walk is held here to the
    /// thread count the tuner resolves. One thread lists every folder on the calling thread, and
    /// many spread them across the pool, which the second walk shows the tree is wide and slow
    /// enough to do.
    /// </summary>
    [Fact]
    public void TheWalkRunsWithTheThreadCountTheTunerResolves()
    {
        for (var i = 0; i < 40; i++)
        {
            _temp.CreateFile(1, "wide", $"folder-{i:D2}", "a.bin");
        }

        var root = Path.Combine(_temp.Path, "wide");

        Assert.Equal(1, ThreadsListing(root, threads: 1));
        Assert.True(ThreadsListing(root, threads: WalkTuning.MaximumThreads) > 1);
    }

    /// <summary>How many threads listed a folder in one walk of <paramref name="root"/>.</summary>
    private static int ThreadsListing(string root, int threads)
    {
        var tuner = new ScanTuner(
            new FakePreferences(AppPreferences.Default with
            {
                Scanning = ScanPreferences.Default.With(StorageMedia.Unknown, new MediaScanPreferences(WalkThreads: threads)),
            }),
            new VolumeMediaCache(new FakeStorageQueries()),
            new FakeVolumeInventory());
        var listing = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();

        BoundedFileWalk.Visit(
            root,
            rootState: 0,
            tuner,
            (_, contents, descend) =>
            {
                listing.TryAdd(Environment.CurrentManagedThreadId, true);

                // Long enough that a pool worker is started while the calling thread is still here.
                Thread.Sleep(5);

                foreach (var entry in contents.Entries.Where(entry => entry.IsDirectory))
                {
                    descend(entry, 0);
                }
            },
            static () => { },
            TimeProvider.System,
            CancellationToken.None);

        return listing.Count;
    }

    [Fact]
    public async Task TheIndexReadsTheTableAsTheSettingsName()
    {
        var volume = Path.GetPathRoot(_temp.Path)![0];
        var sources = new RecordingSources(volume);

        await new DirectoryScanner(sources, tuning: _tuner).MeasureAsync($@"{volume}:\Users");

        Assert.Equal(ReadKiB * 1024, sources.LargestRead);
        Assert.Equal(ReadsInFlight, sources.MostInFlight);
    }

    [Fact]
    public async Task ExploreReadsTheTableAsTheSettingsName()
    {
        var volume = Path.GetPathRoot(_temp.Path)![0];
        var sources = new RecordingSources(volume);

        await new ExploreScanner(sources, tuning: _tuner).ScanAsync($@"{volume}:\");

        Assert.Equal(ReadKiB * 1024, sources.LargestRead);
        Assert.Equal(ReadsInFlight, sources.MostInFlight);
    }

    /// <summary>
    /// One volume serving a table of many reads, noting the largest read it is asked for and the most
    /// reads it is waiting on at once.
    ///
    /// <para>No read completes until more than <see cref="ReadsInFlight"/> are waiting, or half a
    /// second passes with no more, so the most seen is the most the pass makes before it has a
    /// buffer back: exactly what it is allowed, more where it ignores the setting, and fewer where it
    /// holds back. Once let go, every read completes at once.</para>
    /// </summary>
    private sealed class RecordingSources(char volume) : IMftSourceFactory
    {
        private readonly TaskCompletionSource _enoughWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _inFlight;
        private int _mostInFlight;
        private int _largestRead;

        public int LargestRead => Volatile.Read(ref _largestRead);

        public int MostInFlight => Volatile.Read(ref _mostInFlight);

        public IMftSource? TryOpen(char driveLetter, out FallbackReason reason)
        {
            reason = driveLetter == volume ? FallbackReason.None : FallbackReason.NotNtfsVolume;

            return driveLetter == volume ? new Recording(this, Table().Build()) : null;
        }

        private static MftFixture Table()
        {
            var fixture = new MftFixture().AddDirectory(6, MftRecord.RootRecordNumber, "Users");

            // Far more reads than the setting allows in flight, so a pass allowed more would make more.
            for (uint i = 0; i < 400; i++)
            {
                fixture.AddFile(20 + i, 6, $"file-{i:D3}.bin", allocated: 4096, logical: 4000);
            }

            return fixture;
        }

        private void Asked(int bytes)
        {
            var largest = Volatile.Read(ref _largestRead);
            while (bytes > largest && Interlocked.CompareExchange(ref _largestRead, bytes, largest) is var seen && seen != largest)
            {
                largest = seen;
            }
        }

        private sealed class Recording(RecordingSources owner, IMftSource inner) : IMftSource
        {
            public int BytesPerRecord => inner.BytesPerRecord;

            public long RecordCount => inner.RecordCount;

            public int BytesPerCluster => inner.BytesPerCluster;

            public MftBitmapPlacement? Bitmap => inner.Bitmap;

            public int BatchLength(long firstRecord, int capacity) => inner.BatchLength(firstRecord, capacity);

            public int ReadBatch(long firstRecord, Span<byte> destination)
            {
                owner.Asked(destination.Length);
                return inner.ReadBatch(firstRecord, destination);
            }

            public async ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct)
            {
                owner.Asked(destination.Length);

                var waiting = Interlocked.Increment(ref owner._inFlight);
                InterlockedMax(ref owner._mostInFlight, waiting);

                if (waiting > ReadsInFlight)
                {
                    owner._enoughWaiting.TrySetResult();
                }

                if (await Task.WhenAny(owner._enoughWaiting.Task, Task.Delay(TimeSpan.FromMilliseconds(500), ct)).ConfigureAwait(false)
                    != owner._enoughWaiting.Task)
                {
                    owner._enoughWaiting.TrySetResult();
                }

                var read = inner.ReadBatch(firstRecord, destination.Span);
                Interlocked.Decrement(ref owner._inFlight);
                return read;
            }

            public bool TryReadClusters(long firstCluster, Span<byte> destination) =>
                inner.TryReadClusters(firstCluster, destination);

            public void Dispose() => inner.Dispose();

            private static void InterlockedMax(ref int target, int value)
            {
                var current = Volatile.Read(ref target);
                while (value > current && Interlocked.CompareExchange(ref target, value, current) is var seen && seen != current)
                {
                    current = seen;
                }
            }
        }
    }
}
